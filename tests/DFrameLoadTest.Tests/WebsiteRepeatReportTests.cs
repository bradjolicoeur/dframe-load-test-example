using System.Text.Json;
using DFrame;
using Microsoft.Extensions.Logging.Abstractions;

namespace DFrameLoadTest.Tests;

public sealed class WebsiteRepeatReportTests
{
    [Fact]
    public void Merges_repeat_batches_recalculates_percentiles_and_preserves_original_identity()
    {
        var first = Report("first", "a", [10, 20, 30], 2);
        var second = Report("first", "b", [100], 2);
        var third = Report("second", "a", [200], 1) with { RejectedCalls = 3 };
        var report = WebsiteRunReport.CombineGroup([third, second, first]);
        Assert.True(report.Completed);
        Assert.Equal(2, report.Batches.Length);
        Assert.Equal(3, report.WorkloadCount);
        Assert.Equal(3, report.ExpectedWorkloadCount);
        Assert.Equal(5, report.Requests.Length);
        Assert.Equal(3, report.RejectedCalls);
        Assert.Equal(72, report.Pages.Single().AverageMilliseconds);
        Assert.Equal(30, report.Pages.Single().P50Milliseconds);
        Assert.Equal(200, report.Pages.Single().P95Milliseconds);
        Assert.Equal(Enumerable.Range(1, 5), report.Requests.Select(request => request.Sequence));
        Assert.Equal("second", report.Requests.Last().ExecutionId);
        Assert.Equal(1, report.Requests.Last().OriginalSequence);
        Assert.Equal("a", report.Requests.Last().WorkloadId);
        var html = WebsiteHtmlReport.Render(report);
        Assert.Contains("Repeat group: repeat-demo, 2 recorded batches", html);
        Assert.Contains("5 HTTP requests", html);
        Assert.Contains("Repeat batches", html);
        Assert.Contains("do not mean Repeat has finished", html);
    }

    [Fact]
    public void Missing_workloads_and_unfinished_batches_are_partial()
    {
        var report = WebsiteRunReport.CombineGroup([
            Report("first", "a", [10], 2),
            Report("second", "a", [], 1) with { Completed = false }
        ]);
        Assert.False(report.Completed);
        Assert.Equal(3, report.ExpectedWorkloadCount);
        Assert.Equal(2, report.WorkloadCount);
        Assert.All(report.Batches, batch => Assert.False(batch.Completed));
        Assert.Contains("Partial group snapshot", WebsiteHtmlReport.Render(report));
    }

    [Fact]
    public void Rejects_mixed_groups_workers_duplicates_and_unsafe_names()
    {
        var report = Report("first", "a", [10], 1);
        Assert.Throws<InvalidDataException>(() => WebsiteRunReport.CombineGroup([]));
        Assert.Throws<InvalidDataException>(() => WebsiteRunReport.CombineGroup([report, report]));
        Assert.Throws<InvalidDataException>(() => WebsiteRunReport.CombineGroup(
            [report, report with { ReportGroup = "different" }]));
        Assert.Throws<InvalidDataException>(() => WebsiteRunReport.CombineGroup(
            [report, report with { WorkerProcessId = 2 }]));
        Assert.Throws<ArgumentException>(() => WebsiteReportWriter.ValidateGroup("../outside"));
        Assert.Throws<ArgumentException>(() => WebsiteReportWriter.ValidateGroup(""));
        Assert.Throws<ArgumentException>(() => WebsiteReportWriter.ValidateGroup(new string('a', 81)));
    }

    [Fact]
    public async Task Writer_updates_group_without_double_counting_or_mixing_unrelated_runs()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dframe-repeat-tests-{Guid.NewGuid():N}");
        try
        {
            var writer = new WebsiteReportWriter(directory);
            var first = Report("d163fbfe-a694-4537-95b1-67fdb97b73db", "a", [10, 20], 1);
            var second = Report("ce78c63b-956d-43e4-8db5-6879123947e8", "a", [30, 40], 1);
            await Task.WhenAll(new[] { first, second }.Select((report, index) => Task.Run(() =>
            {
                writer.Write($"{index}.json", report with { Completed = false });
                writer.Write($"{index}.json", report);
            })));
            writer.Write("unrelated.json", first with
            {
                ExecutionId = "9a7c3ec6-b04e-4491-93c0-22ce30953c74", ReportGroup = "unrelated"
            });
            writer.Write("ungrouped.json", first with
            {
                ExecutionId = "fc897acb-c980-4bfc-a679-a31fc12df5a0", ReportGroup = null
            });
            var htmlPath = writer.WriteGroup(first);
            var combined = JsonSerializer.Deserialize<WebsiteRunReport>(
                File.ReadAllText(Path.ChangeExtension(htmlPath, ".json")),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(4, combined.Requests.Length);
            Assert.Equal(2, combined.Batches.Length);
            Assert.True(combined.Completed);
            Assert.Contains("4 HTTP requests", File.ReadAllText(htmlPath));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(directory, "groups"), "*.json").Length);
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
    public async Task Workload_parameter_creates_group_report_and_exposes_it_in_details()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dframe-repeat-workload-{Guid.NewGuid():N}");
        using var client = new HttpClient();
        try
        {
            var writer = new WebsiteReportWriter(directory);
            for (var batch = 0; batch < 2; batch++)
            {
                var workload = new WebsiteBrowseWorkload(
                    client, NullLogger<WebsiteBrowseWorkload>.Instance, writer, "repeat-demo");
                var context = new WorkloadContext(default, ExecutionId.New(), 1, 0, 1, 0, default);
                await workload.SetupAsync(context);
                try
                {
                    var details = workload.Complete(context)!;
                    var html = File.ReadAllText(details["Repeat group HTML charts (all batches)"]);
                    Assert.Contains($"repeat-demo, {batch + 1} recorded batches", html);
                }
                finally
                {
                    await workload.TeardownAsync(context);
                }
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static WebsiteRunReport Report(string execution, string workload, double[] timings, int expected)
    {
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        if (execution == "second")
        {
            started = started.AddMinutes(1);
        }
        var requests = timings.Select((timing, index) => new WebsiteRequestResult(
            index + 1, "Homepage", "/", started.AddSeconds(index), 200,
            timing, timing, 0, 100, "Succeeded", null)).ToArray();
        return new WebsiteRunReport(execution, workload, 1, true, 0,
            [WebsitePageSummary.Create("Homepage", "/", requests)], requests)
        {
            ExpectedWorkloadCount = expected,
            ReportGroup = "repeat-demo"
        };
    }
}
