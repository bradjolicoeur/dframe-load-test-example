using System.Text.Json;

namespace DFrameLoadTest.Tests;

public sealed class WebsiteCombinedReportTests
{
    [Fact]
    public void Combines_raw_requests_not_averages_and_keeps_source_identity()
    {
        var first = Report("first", [10, 20, 30], 2) with { RejectedCalls = 1 };
        var second = Report("second", [100], 2) with { RejectedCalls = 2 };
        var combined = WebsiteRunReport.Combine([first, second]);
        Assert.True(combined.IsAggregate);
        Assert.True(combined.Completed);
        Assert.Equal(2, combined.WorkloadCount);
        Assert.Equal(3, combined.RejectedCalls);
        Assert.Equal(4, combined.Requests.Length);
        var page = Assert.Single(combined.Pages);
        Assert.Equal(40, page.AverageMilliseconds);
        Assert.Equal(20, page.P50Milliseconds);
        Assert.Equal(100, page.P95Milliseconds);
        Assert.Equal(4, page.StatusCodes[200]);
        Assert.Equal(3, combined.Requests.Count(request => request.WorkloadId == "first"));
        Assert.Single(combined.Requests, request => request.WorkloadId == "second");
        Assert.Contains("Combined execution: 2 of 2 workload instances", WebsiteHtmlReport.Render(combined));
    }

    [Fact]
    public void Missing_or_incomplete_workloads_keep_aggregate_partial()
    {
        var first = Report("first", [10], 2);
        var missing = WebsiteRunReport.Combine([first]);
        Assert.False(missing.Completed);
        Assert.Equal(2, missing.ExpectedWorkloadCount);
        Assert.False(WebsiteRunReport.Combine(
            [first, Report("second", [], 2) with { Completed = false }]).Completed);
    }

    [Fact]
    public void Rejects_duplicate_workloads_different_executions_workers_and_aggregate_inputs()
    {
        var first = Report("first", [10], 1);
        Assert.Throws<InvalidDataException>(() => WebsiteRunReport.Combine([]));
        Assert.Throws<InvalidDataException>(() => WebsiteRunReport.Combine([first, first]));
        Assert.Throws<InvalidDataException>(() => WebsiteRunReport.Combine(
            [first, first with { WorkloadId = "second", ExecutionId = Guid.NewGuid().ToString() }]));
        Assert.Throws<InvalidDataException>(() => WebsiteRunReport.Combine(
            [first, first with { WorkloadId = "second", WorkerProcessId = 2 }]));
        Assert.Throws<InvalidDataException>(() => WebsiteRunReport.Combine([first with { IsAggregate = true }]));
    }

    [Fact]
    public async Task Concurrent_snapshot_writes_update_combined_files_without_duplicates()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dframe-combined-tests-{Guid.NewGuid():N}");
        try
        {
            var writer = new WebsiteReportWriter(directory);
            var reports = Enumerable.Range(0, 5)
                .Select(index => Report($"workload-{index}", [10, 20, 30, 40], 5)).ToArray();
            await Task.WhenAll(reports.Select((report, index) => Task.Run(() =>
            {
                writer.Write($"{index}.json", report with { Completed = false });
                writer.Write($"{index}.json", report);
            })));
            var path = writer.GetPath(writer.GetCombinedFileName(reports[0]));
            var combined = JsonSerializer.Deserialize<WebsiteRunReport>(
                File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(20, combined.Requests.Length);
            Assert.True(combined.Completed);
            Assert.Equal(5, combined.WorkloadCount);
            Assert.Contains("20 HTTP requests", File.ReadAllText(Path.ChangeExtension(path, ".html")));
            var jsonBefore = File.ReadAllText(Path.Combine(directory, "0.json"));
            writer.WriteCombined(reports[0]);
            Assert.Equal(jsonBefore, File.ReadAllText(Path.Combine(directory, "0.json")));
            Assert.Equal(5, Directory.GetFiles(directory, "*.json").Length);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Legacy_reports_without_expected_count_can_be_combined()
    {
        var combined = WebsiteRunReport.Combine(
            [Report("first", [10], 1), Report("second", [20], 1)]);
        Assert.Equal(2, combined.ExpectedWorkloadCount);
        Assert.True(combined.Completed);
    }

    private static WebsiteRunReport Report(string workload, double[] timings, int expected)
    {
        var requests = timings.Select((timing, index) =>
            new WebsiteRequestResult(index + 1, "Homepage", "/", DateTimeOffset.UtcNow,
                200, timing, timing, 0, 10, "Succeeded", null)).ToArray();
        return new WebsiteRunReport(
            "d163fbfe-a694-4537-95b1-67fdb97b73db", workload, 1, true, 0,
            [WebsitePageSummary.Create("Homepage", "/", requests)], requests)
        {
            ExpectedWorkloadCount = expected
        };
    }
}
