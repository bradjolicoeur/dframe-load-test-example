using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DFrame;

public sealed class WebsiteBrowseWorkload(
    HttpClient httpClient,
    ILogger<WebsiteBrowseWorkload> logger,
    WebsiteReportWriter reportWriter,
    string reportGroup = "") : Workload
{
    private static readonly Uri TargetBaseUri = new("https://bradjolicoeur.com");
    private static readonly (string Name, string Path, string ExpectedContent)[] Steps =
    [
        ("Homepage", "/", "Principal Architect"),
        ("Work With Me", "/work-with-me", "Work With Me"),
        ("Blog", "/Blog", "Blog"),
        ("Resume", "/Resume", "Bradley Jolicoeur")
    ];

    private const int MaxRequestsPerExecution = 30;
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, RunBudget> Runs = new();
    private static long _lastRequestCompletedTimestamp;
    private readonly List<WebsiteRequestResult> _requests = [];
    private readonly string _reportFileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json";
    private RunBudget? _budget;
    private int _rejectedCalls;
    private IDisposable? _activeWorkload;

    public override Task SetupAsync(WorkloadContext context)
    {
        if (!string.IsNullOrWhiteSpace(reportGroup))
        {
            WebsiteReportWriter.ValidateGroup(reportGroup);
        }
        _budget = Runs.GetOrAdd(context.ExecutionId.ToString(), static _ => new RunBudget());
        Interlocked.Increment(ref _budget.WorkloadsRegistered);
        logger.LogInformation(
            "Starting execution {ExecutionId}, workload {WorkloadId}. OTEL run {RunId}, group {ReportGroup}; static reports {StaticReports}",
            context.ExecutionId, context.WorkloadId, WebsiteTelemetry.RunId, reportGroup, reportWriter.Enabled);
        SaveReport(context, completed: false);
        _activeWorkload = HarnessTelemetry.EnterWorkload();
        return Task.CompletedTask;
    }

    public override async Task ExecuteAsync(WorkloadContext context)
    {
        var budget = _budget ?? throw new InvalidOperationException("Run SetupAsync before executing.");
        var waitingStarted = Stopwatch.GetTimestamp();
        using (HarnessTelemetry.EnterQueue())
        {
            try
            {
                await RequestGate.WaitAsync(context.CancellationToken);
            }
            finally
            {
                HarnessTelemetry.RecordWait("queue", waitingStarted);
            }
        }
        try
        {
            if (budget.RequestsStarted >= MaxRequestsPerExecution)
            {
                _rejectedCalls++;
                WebsiteTelemetry.RecordRejection(reportGroup);
                SaveReport(context, completed: false);
                logger.LogWarning(
                    "Execution {ExecutionId}: request cap reached; no HTTP request sent.",
                    context.ExecutionId);
                throw new InvalidOperationException(
                    "The 30-request per-worker limit was reached. Use Request mode with at most 30 total requests.");
            }

            if (_lastRequestCompletedTimestamp != 0)
            {
                var delay = TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(_lastRequestCompletedTimestamp);
                if (delay > TimeSpan.Zero)
                {
                    using var pacing = HarnessTelemetry.EnterPacing();
                    var pacingStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        await Task.Delay(delay, context.CancellationToken);
                    }
                    finally
                    {
                        HarnessTelemetry.RecordWait("pacing", pacingStarted);
                    }
                }
            }

            var sequence = ++budget.RequestsStarted;
            var step = Steps[(sequence - 1) % Steps.Length];
            var waitMilliseconds = Stopwatch.GetElapsedTime(waitingStarted).TotalMilliseconds;
            var startedAt = DateTimeOffset.UtcNow;
            var started = Stopwatch.GetTimestamp();
            int? statusCode = null;
            double? headersMilliseconds = null;
            var responseBytes = 0;
            var outcome = "Succeeded";
            string? error = null;
            using var activity = WebsiteTelemetry.Activities.StartActivity(
                $"GET {step.Path}", ActivityKind.Client);
            activity?.SetTag("test.run", WebsiteTelemetry.RunId);
            activity?.SetTag("test.group", string.IsNullOrWhiteSpace(reportGroup) ? "default" : reportGroup);
            activity?.SetTag("test.execution.id", context.ExecutionId.ToString());
            activity?.SetTag("test.workload.id", context.WorkloadId.ToString());
            activity?.SetTag("test.request.sequence", sequence);
            activity?.SetTag("http.request.method", "GET");
            activity?.SetTag("server.address", TargetBaseUri.Host);
            activity?.SetTag("url.path", step.Path);
            activity?.SetTag("test.step", step.Name);

            logger.LogInformation(
                "Execution {ExecutionId}, workload {WorkloadId}, request {Sequence}: GET {Path} ({Step})",
                context.ExecutionId, context.WorkloadId, sequence, step.Path, step.Name);
            try
            {
                using var inFlight = HarnessTelemetry.EnterHttp();
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(TargetBaseUri, step.Path));
                request.Headers.UserAgent.ParseAdd("DFramePortfolioSiteCheck/1.0");
                request.Headers.Accept.ParseAdd("text/html");
                using var response = await httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken);
                headersMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                statusCode = (int)response.StatusCode;
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsByteArrayAsync(context.CancellationToken);
                responseBytes = body.Length;
                if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html",
                        StringComparison.OrdinalIgnoreCase)
                    || !Encoding.UTF8.GetString(body).Contains(step.ExpectedContent, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"{step.Name}: expected HTML containing '{step.ExpectedContent}'.");
                }
            }
            catch (Exception exception)
            {
                outcome = exception is OperationCanceledException && context.CancellationToken.IsCancellationRequested
                    ? "Canceled"
                    : "Failed";
                error = $"{exception.GetType().Name}: {exception.Message}";
                activity?.SetStatus(ActivityStatusCode.Error, error);
                activity?.SetTag("error.type", exception.GetType().FullName);
                logger.LogError(exception,
                    "Execution {ExecutionId}, request {Sequence}, step {Step}: {Outcome}",
                    context.ExecutionId, sequence, step.Name, outcome);
                throw;
            }
            finally
            {
                var httpMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                _lastRequestCompletedTimestamp = Stopwatch.GetTimestamp();
                var result = new WebsiteRequestResult(
                    sequence, step.Name, step.Path, startedAt, statusCode,
                    headersMilliseconds, httpMilliseconds, waitMilliseconds, responseBytes, outcome, error);
                WebsiteTelemetry.Record(result, reportGroup);
                activity?.SetTag("http.response.status_code", statusCode);
                activity?.SetTag("test.outcome", outcome);
                activity?.SetTag("test.http.duration_ms", httpMilliseconds);
                activity?.SetTag("test.wait.duration_ms", waitMilliseconds);
                activity?.SetTag("http.response.body.size", responseBytes);
                _requests.Add(result);
                SaveReport(context, completed: false);
                logger.LogInformation(
                    "Execution {ExecutionId}, request {Sequence}, step {Step}: {Outcome}, HTTP {StatusCode}, " +
                    "{HttpMilliseconds:F2} ms HTTP, {WaitMilliseconds:F2} ms queue/pacing, {ResponseBytes} bytes",
                    context.ExecutionId, sequence, step.Name, outcome, statusCode,
                    httpMilliseconds, waitMilliseconds, responseBytes);
            }
        }
        finally
        {
            RequestGate.Release();
        }
    }

    public override Dictionary<string, string>? Complete(WorkloadContext context)
    {
        var report = SaveReport(context, completed: true);
        var details = new Dictionary<string, string>
        {
            ["OpenTelemetry run ID"] = WebsiteTelemetry.RunId,
            ["OpenTelemetry group"] = string.IsNullOrWhiteSpace(reportGroup) ? "default" : reportGroup,
            ["Live dashboard"] = "Aspire dashboard: Metrics, Traces, and Structured logs for dframe",
            ["HTTP requests sent"] = _requests.Count.ToString(CultureInfo.InvariantCulture),
            ["Rejected calls (cap reached; no HTTP)"] = _rejectedCalls.ToString(CultureInfo.InvariantCulture),
            ["Timing scope"] = "HTTP timings exclude queue/pacing; DFrame execution timings include both."
        };
        foreach (var page in report.Pages)
        {
            details[$"{page.Step} ({page.Path})"] = FormattableString.Invariant(
                $"sent={page.Requests}, ok={page.Succeeded}, failed={page.Failed}, canceled={page.Canceled}; HTTP ms: min={page.MinMilliseconds:F2}, avg={page.AverageMilliseconds:F2}, max={page.MaxMilliseconds:F2}, p50={page.P50Milliseconds:F2}, p95={page.P95Milliseconds:F2}, p99={page.P99Milliseconds:F2}");
        }
        if (reportWriter.Enabled)
        {
            details["Report (this workload)"] = ReportPath;
            details["HTML charts (open in browser)"] = reportWriter.GetHtmlPath(_reportFileName);
            details["Combined HTML charts (all concurrency)"] =
                reportWriter.GetHtmlPath(reportWriter.GetCombinedFileName(report));
            logger.LogInformation("Execution {ExecutionId} static report saved: {ReportPath}", context.ExecutionId, ReportPath);
            if (!string.IsNullOrEmpty(report.ReportGroup))
            {
                var groupPath = reportWriter.GetHtmlPath(reportWriter.GetGroupFileName(report));
                details["Repeat group HTML charts (all batches)"] = groupPath;
                logger.LogInformation("Repeat report group {ReportGroup}: {GroupPath}", report.ReportGroup, groupPath);
            }
        }
        return details;
    }

    public override Task TeardownAsync(WorkloadContext context)
    {
        _activeWorkload?.Dispose();
        _activeWorkload = null;
        if (_budget is not null
            && Interlocked.Increment(ref _budget.WorkloadsTornDown) >= Volatile.Read(ref _budget.WorkloadsRegistered))
        {
            Runs.TryRemove(context.ExecutionId.ToString(), out _);
        }
        _budget = null;
        return Task.CompletedTask;
    }

    private string ReportPath => reportWriter.GetPath(_reportFileName);

    private WebsiteRunReport SaveReport(WorkloadContext context, bool completed)
    {
        var pages = Steps.Select(step => WebsitePageSummary.Create(
            step.Name, step.Path, _requests.Where(request => request.Path == step.Path).ToArray())).ToArray();
        var report = new WebsiteRunReport(
            context.ExecutionId.ToString(), context.WorkloadId.ToString(), Environment.ProcessId,
            completed, _rejectedCalls, pages, _requests.ToArray())
        {
            ExpectedWorkloadCount = Math.Max(context.WorkloadCount,
                _budget is null ? 0 : Volatile.Read(ref _budget.WorkloadsRegistered)),
            ReportGroup = string.IsNullOrWhiteSpace(reportGroup) ? null : reportGroup
        };
        if (reportWriter.Enabled)
        {
            reportWriter.Write(_reportFileName, report);
        }
        return report;
    }

    private sealed class RunBudget
    {
        public int RequestsStarted;
        public int WorkloadsTornDown;
        public int WorkloadsRegistered;
    }
}
