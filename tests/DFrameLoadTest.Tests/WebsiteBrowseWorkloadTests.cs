using System.Net;
using System.Text;
using System.Text.Json;
using DFrame;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DFrameLoadTest.Tests;

public sealed class WebsiteBrowseWorkloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"dframe-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Records_named_steps_timings_validation_and_saved_reports()
    {
        var paths = new List<string>();
        using var client = new HttpClient(new StubHandler(async (request, token) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Equal("bradjolicoeur.com", request.RequestUri.Host);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("DFramePortfolioSiteCheck", request.Headers.UserAgent.ToString());
            await Task.Delay(20, token);
            return Html("Principal Architect; Work With Me; Blog; Bradley Jolicoeur");
        }));
        var workload = CreateWorkload(client);
        var context = CreateContext();
        await workload.SetupAsync(context);
        try
        {
            for (var index = 0; index < 4; index++)
            {
                await workload.ExecuteAsync(context);
            }

            var partial = ReadReports().Single();
            Assert.False(partial.Completed);
            Assert.Equal(4, partial.Requests.Length);
            var details = workload.Complete(context)!;
            var report = ReadReports().Single();
            Assert.True(report.Completed);
            Assert.Equal(["/", "/work-with-me", "/Blog", "/Resume"], paths);
            Assert.Equal(4, report.Pages.Length);
            Assert.All(report.Pages, page =>
            {
                Assert.Equal(1, page.Requests);
                Assert.Equal(1, page.Succeeded);
                Assert.Equal(0, page.Failed);
                Assert.Equal(1, page.StatusCodes[200]);
                Assert.True(page.P95Milliseconds > 0);
            });
            Assert.All(report.Requests, request =>
            {
                Assert.Equal("Succeeded", request.Outcome);
                Assert.True(request.ResponseBytes > 0);
                Assert.True(request.HeadersMilliseconds <= request.HttpMilliseconds);
                Assert.Null(request.Error);
            });
            Assert.All(report.Requests.Skip(1), request => Assert.True(request.WaitMilliseconds >= 900));
            Assert.Contains("p95=", details["Homepage (/)"]);
            Assert.True(File.Exists(details["Report (this workload)"]));
            Assert.True(File.Exists(details["HTML charts (open in browser)"]));
            Assert.Contains("Per-page HTTP latency", File.ReadAllText(details["HTML charts (open in browser)"]));
        }
        finally
        {
            await workload.TeardownAsync(context);
        }
    }

    [Theory]
    [InlineData("status", "HttpRequestException", 503, "Failed")]
    [InlineData("redirect", "HttpRequestException", 302, "Failed")]
    [InlineData("content", "InvalidDataException", 200, "Failed")]
    [InlineData("media-type", "InvalidDataException", 200, "Failed")]
    [InlineData("network", "HttpRequestException", null, "Failed")]
    [InlineData("timeout", "TaskCanceledException", null, "Failed")]
    [InlineData("cancel", "TaskCanceledException", null, "Canceled")]
    public async Task Records_errors_and_preserves_exceptions(
        string failure, string exceptionType, int? statusCode, string outcome)
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new StubHandler((_, token) =>
        {
            switch (failure)
            {
                case "network":
                    throw new HttpRequestException("Simulated network failure");
                case "timeout":
                    throw new TaskCanceledException("Simulated timeout");
                case "cancel":
                    cancellation.Cancel();
                    throw new OperationCanceledException(token);
                case "status":
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                case "redirect":
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found));
                case "media-type":
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("Principal Architect", Encoding.UTF8, "text/plain")
                    });
                default:
                    return Task.FromResult(Html("Unrelated page"));
            }
        }));
        var workload = CreateWorkload(client);
        var context = CreateContext(token: cancellation.Token);
        await workload.SetupAsync(context);
        try
        {
            var exception = await Record.ExceptionAsync(() => workload.ExecuteAsync(context));
            Assert.NotNull(exception);
            Assert.Contains(exceptionType, exception.GetType().Name);
            var request = ReadReports().Single().Requests.Single();
            Assert.Equal(statusCode, request.StatusCode);
            Assert.Equal(outcome, request.Outcome);
            Assert.Contains(exceptionType, request.Error);
            if (statusCode is null)
            {
                Assert.Null(request.HeadersMilliseconds);
            }
        }
        finally
        {
            await workload.TeardownAsync(context);
        }
    }

    [Fact]
    public async Task Shares_cap_between_workloads_and_rejects_unsent_calls_without_success()
    {
        var sent = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref sent);
            return Task.FromResult(Html("Principal Architect; Work With Me; Blog; Bradley Jolicoeur"));
        }));
        var first = CreateWorkload(client);
        var second = CreateWorkload(client);
        var executionId = ExecutionId.New();
        var firstContext = CreateContext(executionId, count: 2);
        var secondContext = CreateContext(executionId, count: 2, index: 1);
        await first.SetupAsync(firstContext);
        await second.SetupAsync(secondContext);
        try
        {
            for (var index = 0; index < 15; index++)
            {
                await Task.WhenAll(first.ExecuteAsync(firstContext), second.ExecuteAsync(secondContext));
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => first.ExecuteAsync(firstContext));
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.ExecuteAsync(secondContext));
            Assert.Equal(30, sent);
            first.Complete(firstContext);
            second.Complete(secondContext);
            var reports = ReadReports();
            Assert.Equal(2, reports.Length);
            Assert.All(reports, report =>
            {
                Assert.Equal(15, report.Requests.Length);
                Assert.Equal(1, report.RejectedCalls);
                Assert.Equal(15, report.Pages.Sum(page => page.Succeeded));
            });
            Assert.Equal(Enumerable.Range(1, 30),
                reports.SelectMany(report => report.Requests).Select(request => request.Sequence).Order());
        }
        finally
        {
            await first.TeardownAsync(firstContext);
            await second.TeardownAsync(secondContext);
        }
    }

    [Fact]
    public async Task Virtual_worker_teardown_keeps_budget_and_expected_count_for_other_workers()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(Html("Principal Architect; Work With Me; Blog; Bradley Jolicoeur"))));
        var executionId = ExecutionId.New();
        var first = CreateWorkload(client);
        var second = CreateWorkload(client);
        var firstContext = CreateContext(executionId);
        var secondContext = CreateContext(executionId);
        await first.SetupAsync(firstContext);
        await second.SetupAsync(secondContext);
        await first.ExecuteAsync(firstContext);
        first.Complete(firstContext);
        await first.TeardownAsync(firstContext);
        try
        {
            await second.ExecuteAsync(secondContext);
            second.Complete(secondContext);
            var combinedPath = Directory.GetFiles(Path.Combine(_directory, "combined"), "*.json").Single();
            var combined = JsonSerializer.Deserialize<WebsiteRunReport>(
                File.ReadAllText(combinedPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(2, combined.ExpectedWorkloadCount);
            Assert.True(combined.Completed);
            Assert.Equal([1, 2], combined.Requests.Select(request => request.Sequence));
        }
        finally
        {
            await second.TeardownAsync(secondContext);
        }
    }

    [Fact]
    public void Summarizes_nearest_rank_percentiles_and_empty_pages()
    {
        var requests = new[] { 10d, 20d, 30d, 40d }.Select((elapsed, index) =>
            new WebsiteRequestResult(index + 1, "Homepage", "/", DateTimeOffset.UtcNow,
                200, 1, elapsed, 1000, 100, "Succeeded", null)).ToArray();
        var summary = WebsitePageSummary.Create("Homepage", "/", requests);
        Assert.Equal(10, summary.MinMilliseconds);
        Assert.Equal(25, summary.AverageMilliseconds);
        Assert.Equal(40, summary.MaxMilliseconds);
        Assert.Equal(20, summary.P50Milliseconds);
        Assert.Equal(40, summary.P95Milliseconds);
        Assert.Equal(40, summary.P99Milliseconds);
        var empty = WebsitePageSummary.Create("Blog", "/Blog", []);
        Assert.Equal(0, empty.Requests);
        Assert.Null(empty.AverageMilliseconds);
        Assert.Null(empty.P95Milliseconds);
    }

    [Fact]
    public async Task Cancellation_before_sending_does_not_count_a_success_or_send_traffic()
    {
        var sent = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            sent++;
            return Task.FromResult(Html("Principal Architect"));
        }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = CreateContext(token: cancellation.Token);
        var workload = CreateWorkload(client);
        await workload.SetupAsync(context);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workload.ExecuteAsync(context));
            Assert.Equal(0, sent);
            Assert.Empty(ReadReports().Single().Requests);
        }
        finally
        {
            await workload.TeardownAsync(context);
        }
    }

    [Fact]
    public void Report_write_failures_are_not_suppressed()
    {
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(file, "A file cannot be used as the report directory.");
        var writer = new WebsiteReportWriter(file);
        var report = new WebsiteRunReport("execution", "workload", 1, false, 0, [], []);
        Assert.Throws<IOException>(() => writer.Write("report.json", report));
    }

    private WebsiteBrowseWorkload CreateWorkload(HttpClient client) =>
        new(client, NullLogger<WebsiteBrowseWorkload>.Instance, new WebsiteReportWriter(_directory));

    private WebsiteRunReport[] ReadReports() =>
        Directory.GetFiles(_directory, "*.json").Select(path =>
            JsonSerializer.Deserialize<WebsiteRunReport>(
                File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();

    private static WorkloadContext CreateContext(
        ExecutionId? executionId = null, int count = 1, int index = 0, CancellationToken token = default) =>
        new(default, executionId ?? ExecutionId.New(), count, index, 1, 30, token);

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html")
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
