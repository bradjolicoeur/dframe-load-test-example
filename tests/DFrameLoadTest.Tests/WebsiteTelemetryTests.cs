using System.Diagnostics;
using System.Net;
using DFrame;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;

namespace DFrameLoadTest.Tests;

public sealed class WebsiteTelemetryTests
{
    [Fact]
    public async Task Production_provider_exports_OTLP_to_a_receiver()
    {
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var receiver = builder.Build();
        receiver.MapPost("/v1/metrics", async context =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            var payload = body.ToArray();
            if (Environment.GetEnvironmentVariable("OTEL_TEST_COLLECTOR_ENDPOINT") is { Length: > 0 } endpoint)
            {
                using var client = new HttpClient();
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new("application/x-protobuf");
                using var response = await client.PostAsync($"{endpoint.TrimEnd('/')}/v1/metrics", content);
                response.EnsureSuccessStatusCode();
            }
            received.TrySetResult(payload);
            context.Response.ContentType = "application/x-protobuf";
        });
        await receiver.StartAsync();
        try
        {
            var address = receiver.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.Services.AddWebsiteTelemetry();
            hostBuilder.Services.Configure<OtlpExporterOptions>(options =>
            {
                options.Endpoint = new Uri($"{address}/v1/metrics");
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
            });
            using var host = hostBuilder.Build();
            await host.StartAsync();
            try
            {
                WebsiteTelemetry.Record(new WebsiteRequestResult(
                    1, "Homepage", "/", DateTimeOffset.UtcNow, 200, 20, 100, 1000, 42,
                    "Succeeded", null), "otlp-test");
                Assert.True(host.Services.GetRequiredService<MeterProvider>().ForceFlush());
                var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var text = System.Text.Encoding.UTF8.GetString(payload);
                Assert.Contains("website.requests", text);
                Assert.Contains("website.http.duration", text);
                Assert.Contains("test.run", text);
                Assert.Contains("otlp-test", text);
                Assert.Contains("harness.process.cpu.capacity", text);
                Assert.Contains("dotnet.process.cpu.time", text);
                Assert.Contains("dotnet.process.memory.working_set", text);
            }
            finally
            {
                await host.StopAsync();
            }
        }
        finally
        {
            await receiver.StopAsync();
        }
    }

    [Fact]
    public void Metrics_merge_requests_without_workload_or_batch_id_labels_and_separate_rejections()
    {
        var exported = new List<Metric>();
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(WebsiteTelemetry.MeterName)
            .AddInMemoryExporter(exported).Build();
        var request = new WebsiteRequestResult(
            1, "Homepage", "/", DateTimeOffset.UtcNow, 200, 20, 100, 1000, 42, "Succeeded", null);
        WebsiteTelemetry.Record(request with { WorkloadId = "one", ExecutionId = "batch-one" }, "repeat");
        WebsiteTelemetry.Record(request with { WorkloadId = "two", ExecutionId = "batch-two" }, "repeat");
        WebsiteTelemetry.Record(request with
        {
            StatusCode = null, HeadersMilliseconds = null, Outcome = "Failed", Error = "Network error"
        }, "repeat");
        WebsiteTelemetry.RecordRejection("repeat");
        Assert.True(provider.ForceFlush());

        var counts = exported.Single(metric => metric.Name == "website.requests");
        var successCount = 0L;
        var failedCount = 0L;
        foreach (ref readonly var point in counts.GetMetricPoints())
        {
            var tags = ReadTags(point);
            Assert.Equal(WebsiteTelemetry.RunId, tags["test.run"]);
            Assert.Equal("repeat", tags["test.group"]);
            Assert.DoesNotContain("test.execution.id", tags.Keys);
            Assert.DoesNotContain("test.workload.id", tags.Keys);
            if (Equals(tags["outcome"], "Succeeded"))
            {
                successCount += point.GetSumLong();
            }
            else
            {
                failedCount += point.GetSumLong();
                Assert.Equal("none", tags["http.response.status_code"]);
            }
        }
        Assert.Equal(2, successCount);
        Assert.Equal(1, failedCount);
        AssertHistogram(exported, "website.http.duration", 3, 0.3);
        AssertHistogram(exported, "website.headers.duration", 2, 0.04);
        AssertHistogram(exported, "website.wait.duration", 3, 3);
        AssertHistogram(exported, "website.response.size", 3, 126);
        var rejectionCount = 0L;
        foreach (ref readonly var point in exported.Single(metric => metric.Name == "website.rejected").GetMetricPoints())
        {
            rejectionCount += point.GetSumLong();
            Assert.Equal("request_cap", ReadTags(point)["reason"]);
        }
        Assert.Equal(1, rejectionCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Workload_emits_correlated_traces_and_metrics_without_static_reports(bool success)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dframe-no-reports-{Guid.NewGuid():N}");
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == WebsiteTelemetry.MeterName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        var metrics = new List<Metric>();
        using var provider = Sdk.CreateMeterProviderBuilder().AddMeter(WebsiteTelemetry.MeterName)
            .AddInMemoryExporter(metrics).Build();
        using var client = new HttpClient(new StubHandler(success));
        var workload = new WebsiteBrowseWorkload(client, NullLogger<WebsiteBrowseWorkload>.Instance,
            new WebsiteReportWriter(directory, enabled: false), "repeat");
        var context = new WorkloadContext(default, ExecutionId.New(), 1, 0, 1, 1, default);
        await workload.SetupAsync(context);
        try
        {
            if (success)
            {
                await workload.ExecuteAsync(context);
            }
            else
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => workload.ExecuteAsync(context));
            }
            var details = workload.Complete(context)!;
            Assert.False(Directory.Exists(directory));
            Assert.DoesNotContain("Report (this workload)", details.Keys);
            Assert.Equal(WebsiteTelemetry.RunId, details["OpenTelemetry run ID"]);
            var activity = Assert.Single(activities);
            Assert.Equal("GET /", activity.DisplayName);
            Assert.Equal(context.ExecutionId.ToString(), activity.GetTagItem("test.execution.id"));
            Assert.Equal(context.WorkloadId.ToString(), activity.GetTagItem("test.workload.id"));
            Assert.Equal(success ? "Succeeded" : "Failed", activity.GetTagItem("test.outcome"));
            Assert.Equal(success ? ActivityStatusCode.Unset : ActivityStatusCode.Error, activity.Status);
            Assert.True(provider.ForceFlush());
            AssertHistogram(metrics, "website.http.duration", 1, null);
        }
        finally
        {
            await workload.TeardownAsync(context);
        }
    }

    private static Dictionary<string, object?> ReadTags(in MetricPoint point)
    {
        var tags = new Dictionary<string, object?>();
        foreach (var tag in point.Tags)
        {
            tags.Add(tag.Key, tag.Value);
        }
        return tags;
    }

    private static void AssertHistogram(List<Metric> metrics, string name, long count, double? sum)
    {
        var actualCount = 0L;
        var actualSum = 0d;
        foreach (ref readonly var point in metrics.Single(metric => metric.Name == name).GetMetricPoints())
        {
            actualCount += point.GetHistogramCount();
            actualSum += point.GetHistogramSum();
        }
        Assert.Equal(count, actualCount);
        if (sum is { } expected)
        {
            Assert.Equal(expected, actualSum, precision: 6);
        }
    }

    private sealed class StubHandler(bool success) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(
            new HttpResponseMessage(success ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("Principal Architect", System.Text.Encoding.UTF8, "text/html")
            });
    }
}
