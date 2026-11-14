using System.Text.Json;

public sealed class WebsiteReportWriter(string directory, bool enabled = true)
{
    public bool Enabled { get; } = enabled;
    private static readonly object WriteLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string GetPath(string fileName) => Path.GetFullPath(Path.Combine(directory, fileName));
    public string GetHtmlPath(string fileName) => Path.ChangeExtension(GetPath(fileName), ".html");
    public string GetCombinedFileName(WebsiteRunReport report) =>
        Path.Combine("combined", $"{Guid.Parse(report.ExecutionId):D}-{report.WorkerProcessId}.json");
    public string GetGroupFileName(WebsiteRunReport report)
    {
        ValidateGroup(report.ReportGroup);
        return Path.Combine("groups", $"{report.ReportGroup}-{report.WorkerProcessId}.json");
    }

    public static void ValidateGroup(string? group)
    {
        if (string.IsNullOrEmpty(group) || group.Length > 80
            || group.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("Report group must be 1-80 ASCII letters, digits, or hyphens.");
        }
    }

    public void Write(string fileName, WebsiteRunReport report)
    {
        lock (WriteLock)
        {
            WriteSnapshot(fileName, report);
            WriteCombined(report);
            if (!string.IsNullOrEmpty(report.ReportGroup))
            {
                WriteGroup(report);
            }
        }
    }

    public string WriteGroup(WebsiteRunReport source)
    {
        lock (WriteLock)
        {
            ValidateGroup(source.ReportGroup);
            var reports = Directory.EnumerateFiles(directory, "*.json")
                .Select(path => JsonSerializer.Deserialize<WebsiteRunReport>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException($"No run report in {path}."))
                .Where(report => !report.IsAggregate && report.ReportGroup == source.ReportGroup
                    && report.WorkerProcessId == source.WorkerProcessId)
                .ToArray();
            var group = WebsiteRunReport.CombineGroup(reports);
            var fileName = GetGroupFileName(group);
            WriteSnapshot(fileName, group);
            return GetHtmlPath(fileName);
        }
    }

    public string WriteCombined(WebsiteRunReport source)
    {
        lock (WriteLock)
        {
            var reports = Directory.EnumerateFiles(directory, "*.json")
                .Select(path => JsonSerializer.Deserialize<WebsiteRunReport>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException($"No run report in {path}."))
                .Where(report => !report.IsAggregate && report.ExecutionId == source.ExecutionId
                    && report.WorkerProcessId == source.WorkerProcessId)
                .ToArray();
            var combined = WebsiteRunReport.Combine(reports);
            var fileName = GetCombinedFileName(combined);
            WriteSnapshot(fileName, combined);
            return GetHtmlPath(fileName);
        }
    }

    private void WriteSnapshot(string fileName, WebsiteRunReport report)
    {
        var path = GetPath(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(report, JsonOptions));
        File.Move(path + ".tmp", path, overwrite: true);
        WriteHtml(fileName, report);
    }

    public void WriteHtml(string fileName, WebsiteRunReport report)
    {
        lock (WriteLock)
        {
            var path = GetHtmlPath(fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", WebsiteHtmlReport.Render(report));
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }
}

public sealed record WebsiteRunReport(
    string ExecutionId,
    string WorkloadId,
    int WorkerProcessId,
    bool Completed,
    int RejectedCalls,
    WebsitePageSummary[] Pages,
    WebsiteRequestResult[] Requests)
{
    public bool IsAggregate { get; init; }
    public int WorkloadCount { get; init; } = 1;
    public int ExpectedWorkloadCount { get; init; } = 1;
    public string? ReportGroup { get; init; }
    public WebsiteBatchSummary[] Batches { get; init; } = [];

    public static WebsiteRunReport CombineGroup(WebsiteRunReport[] reports)
    {
        if (reports.Length == 0
            || reports.Select(report => (report.ReportGroup, report.WorkerProcessId)).Distinct().Count() != 1)
        {
            throw new InvalidDataException("A report group requires reports from one named group and worker process.");
        }
        WebsiteReportWriter.ValidateGroup(reports[0].ReportGroup);
        var batches = reports.GroupBy(report => report.ExecutionId)
            .Select(batch => Combine(batch.ToArray()))
            .OrderBy(batch => batch.Requests.Select(request => request.StartedAt)
                .DefaultIfEmpty(DateTimeOffset.MaxValue).Min())
            .ThenBy(batch => batch.ExecutionId)
            .ToArray();
        var requests = reports.SelectMany(report => report.Requests.Select(request => request with
            {
                ExecutionId = report.ExecutionId,
                WorkloadId = report.WorkloadId,
                OriginalSequence = request.Sequence
            }))
            .OrderBy(request => request.StartedAt)
            .ThenBy(request => request.ExecutionId)
            .ThenBy(request => request.Sequence)
            .Select((request, index) => request with { Sequence = index + 1 }).ToArray();
        var pages = reports.SelectMany(report => report.Pages).DistinctBy(page => page.Path)
            .Select(page => WebsitePageSummary.Create(
                page.Step, page.Path, requests.Where(request => request.Path == page.Path).ToArray())).ToArray();
        return new WebsiteRunReport(
            "multiple batches", "combined", reports[0].WorkerProcessId,
            batches.All(batch => batch.Completed), reports.Sum(report => report.RejectedCalls), pages, requests)
        {
            IsAggregate = true,
            ReportGroup = reports[0].ReportGroup,
            WorkloadCount = batches.Sum(batch => batch.WorkloadCount),
            ExpectedWorkloadCount = batches.Sum(batch => batch.ExpectedWorkloadCount),
            Batches = batches.Select(batch => new WebsiteBatchSummary(
                batch.ExecutionId, batch.Completed, batch.WorkloadCount, batch.ExpectedWorkloadCount,
                batch.Requests.Length, batch.RejectedCalls, batch.Pages)).ToArray()
        };
    }

    public static WebsiteRunReport Combine(WebsiteRunReport[] reports)
    {
        if (reports.Length == 0 || reports.Any(report => report.IsAggregate)
            || reports.Select(report => (report.ExecutionId, report.WorkerProcessId)).Distinct().Count() != 1
            || reports.Select(report => report.WorkloadId).Distinct().Count() != reports.Length)
        {
            throw new InvalidDataException(
                "Combine distinct workload reports from exactly one execution and worker process.");
        }

        var requests = reports.SelectMany(report => report.Requests.Select(request =>
                request with { WorkloadId = report.WorkloadId }))
            .OrderBy(request => request.Sequence).ToArray();
        var pages = reports.SelectMany(report => report.Pages)
            .DistinctBy(page => page.Path)
            .Select(page => WebsitePageSummary.Create(
                page.Step, page.Path, requests.Where(request => request.Path == page.Path).ToArray()))
            .ToArray();
        var expected = Math.Max(reports.Length, reports.Max(report => report.ExpectedWorkloadCount));
        return new WebsiteRunReport(
            reports[0].ExecutionId, "combined", reports[0].WorkerProcessId,
            reports.Length == expected && reports.All(report => report.Completed),
            reports.Sum(report => report.RejectedCalls), pages, requests)
        {
            IsAggregate = true,
            WorkloadCount = reports.Length,
            ExpectedWorkloadCount = expected
        };
    }
}

public sealed record WebsiteRequestResult(
    int Sequence,
    string Step,
    string Path,
    DateTimeOffset StartedAt,
    int? StatusCode,
    double? HeadersMilliseconds,
    double HttpMilliseconds,
    double WaitMilliseconds,
    int ResponseBytes,
    string Outcome,
    string? Error)
{
    public string? WorkloadId { get; init; }
    public string? ExecutionId { get; init; }
    public int? OriginalSequence { get; init; }
}

public sealed record WebsiteBatchSummary(
    string ExecutionId,
    bool Completed,
    int WorkloadCount,
    int ExpectedWorkloadCount,
    int Requests,
    int RejectedCalls,
    WebsitePageSummary[] Pages);

public sealed record WebsitePageSummary(
    string Step,
    string Path,
    int Requests,
    int Succeeded,
    int Failed,
    int Canceled,
    Dictionary<int, int> StatusCodes,
    double? MinMilliseconds,
    double? AverageMilliseconds,
    double? MaxMilliseconds,
    double? P50Milliseconds,
    double? P95Milliseconds,
    double? P99Milliseconds)
{
    public static WebsitePageSummary Create(string step, string path, WebsiteRequestResult[] requests)
    {
        var timings = requests.Select(request => request.HttpMilliseconds).Order().ToArray();
        return new WebsitePageSummary(
            step, path, requests.Length,
            requests.Count(request => request.Outcome == "Succeeded"),
            requests.Count(request => request.Outcome == "Failed"),
            requests.Count(request => request.Outcome == "Canceled"),
            requests.Where(request => request.StatusCode.HasValue)
                .GroupBy(request => request.StatusCode!.Value)
                .ToDictionary(group => group.Key, group => group.Count()),
            timings.Length == 0 ? null : timings[0],
            timings.Length == 0 ? null : timings.Average(),
            timings.Length == 0 ? null : timings[^1],
            Percentile(timings, 0.50), Percentile(timings, 0.95), Percentile(timings, 0.99));
    }

    private static double? Percentile(double[] sorted, double percentile) =>
        sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];
}
