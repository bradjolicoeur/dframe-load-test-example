using System.Diagnostics;
using System.Net;
using System.Text;
using DFrame;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DFrameLoadTest.Tests;

public sealed class WebsiteBrowseWorkloadTests
{
    [Fact]
    public async Task Sends_named_steps_and_preserves_execution_results()
    {
        var paths = new List<string>();
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("bradjolicoeur.com", request.RequestUri.Host);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("DFramePortfolioSiteCheck", request.Headers.UserAgent.ToString());
            paths.Add(request.RequestUri.AbsolutePath);
            return Task.FromResult(Html("Principal Architect; Work With Me; Blog; Bradley Jolicoeur"));
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
            Assert.Equal(["/", "/work-with-me", "/Blog", "/Resume"], paths);
            var details = workload.Complete(context)!;
            Assert.Equal("4", details["HTTP requests sent"]);
            Assert.Equal(WebsiteTelemetry.RunId, details["OpenTelemetry run ID"]);
            Assert.Equal("default", details["OpenTelemetry group"]);
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
                case "network": throw new HttpRequestException("Simulated network failure");
                case "timeout": throw new TaskCanceledException("Simulated timeout");
                case "cancel":
                    cancellation.Cancel();
                    throw new OperationCanceledException(token);
                case "status": return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                case "redirect": return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found));
                case "media-type":
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("Principal Architect", Encoding.UTF8, "text/plain")
                    });
                default: return Task.FromResult(Html("Unrelated page"));
            }
        }));
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == WebsiteTelemetry.MeterName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);
        var workload = CreateWorkload(client);
        var context = CreateContext(token: cancellation.Token);
        await workload.SetupAsync(context);
        try
        {
            var exception = await Record.ExceptionAsync(() => workload.ExecuteAsync(context));
            Assert.NotNull(exception);
            Assert.Contains(exceptionType, exception.GetType().Name);
            var span = Assert.Single(activities);
            Assert.Equal(outcome, span.GetTagItem("test.outcome"));
            Assert.Equal(statusCode, span.GetTagItem("http.response.status_code"));
            Assert.Equal(ActivityStatusCode.Error, span.Status);
            Assert.Equal("1", workload.Complete(context)!["HTTP requests sent"]);
        }
        finally
        {
            await workload.TeardownAsync(context);
        }
    }

    [Fact]
    public async Task Requests_across_workloads_use_one_sequence_without_a_cap()
    {
        var sent = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref sent);
            return Task.FromResult(Html("Principal Architect; Work With Me; Blog; Bradley Jolicoeur"));
        }));
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == WebsiteTelemetry.MeterName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (activities) activities.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        var first = CreateWorkload(client);
        var second = CreateWorkload(client);
        var executionId = ExecutionId.New();
        var firstContext = CreateContext(executionId, count: 2);
        var secondContext = CreateContext(executionId, count: 2, index: 1);
        await first.SetupAsync(firstContext);
        await second.SetupAsync(secondContext);
        try
        {
            for (var index = 0; index < 20; index++)
            {
                await Task.WhenAll(first.ExecuteAsync(firstContext), second.ExecuteAsync(secondContext));
            }
            Assert.Equal(40, sent);
            Assert.Equal("20", first.Complete(firstContext)!["HTTP requests sent"]);
            Assert.Equal("20", second.Complete(secondContext)!["HTTP requests sent"]);
            Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i),
                activities.Select(activity => (long)activity.GetTagItem("test.request.sequence")!).Order());
        }
        finally
        {
            await first.TeardownAsync(firstContext);
            await second.TeardownAsync(secondContext);
        }
    }

    [Fact]
    public async Task Teardown_of_one_worker_preserves_sequence_for_the_other()
    {
        var paths = new List<string>();
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Html("Principal Architect; Work With Me; Blog; Bradley Jolicoeur"));
        }));
        var execution = ExecutionId.New();
        var first = CreateWorkload(client);
        var second = CreateWorkload(client);
        var firstContext = CreateContext(execution);
        var secondContext = CreateContext(execution);
        await first.SetupAsync(firstContext);
        await second.SetupAsync(secondContext);
        await first.ExecuteAsync(firstContext);
        await first.TeardownAsync(firstContext);
        try
        {
            await second.ExecuteAsync(secondContext);
            Assert.Equal(["/", "/work-with-me"], paths);
        }
        finally
        {
            await second.TeardownAsync(secondContext);
        }
    }

    [Fact]
    public async Task Cancellation_before_sending_does_not_send_traffic()
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
            Assert.Equal("0", workload.Complete(context)!["HTTP requests sent"]);
        }
        finally
        {
            await workload.TeardownAsync(context);
        }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("invalid group")]
    public async Task Rejects_invalid_telemetry_groups(string group)
    {
        using var client = new HttpClient();
        var workload = new WebsiteBrowseWorkload(client, NullLogger<WebsiteBrowseWorkload>.Instance, group);
        await Assert.ThrowsAsync<ArgumentException>(() => workload.SetupAsync(CreateContext()));
    }

    private static WebsiteBrowseWorkload CreateWorkload(HttpClient client) =>
        new(client, NullLogger<WebsiteBrowseWorkload>.Instance);

    private static WorkloadContext CreateContext(
        ExecutionId? executionId = null, int count = 1, int index = 0, CancellationToken token = default) =>
        new(default, executionId ?? ExecutionId.New(), count, index, 1, 30, token);

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html")
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
