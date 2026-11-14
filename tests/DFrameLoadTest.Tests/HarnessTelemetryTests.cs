using System.Net;
using DFrame;
using DFrameControllerExecutionEngine = DFrame.Controller.DFrameControllerExecutionEngine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MessagePipe;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace DFrameLoadTest.Tests;

public sealed class HarnessTelemetryTests
{
    [Fact]
    public void Gauges_track_scopes_and_release_exactly_once()
    {
        HarnessTelemetry.Initialize();
        var metrics = new List<Metric>();
        using var provider = Sdk.CreateMeterProviderBuilder().AddMeter(HarnessTelemetry.MeterName, "System.Runtime")
            .AddInMemoryExporter(metrics).Build();
        using var workload = HarnessTelemetry.EnterWorkload();
        using var queue = HarnessTelemetry.EnterQueue();
        using var pacing = HarnessTelemetry.EnterPacing();
        using var http = HarnessTelemetry.EnterHttp();
        Assert.True(provider.ForceFlush());
        Assert.Equal(1, Gauge(metrics, "harness.workloads.active"));
        Assert.Equal(1, Gauge(metrics, "harness.requests.queued"));
        Assert.Equal(1, Gauge(metrics, "harness.requests.pacing"));
        Assert.Equal(1, Gauge(metrics, "harness.requests.in_flight"));
        Assert.Equal(Environment.ProcessorCount, Gauge(metrics, "harness.process.cpu.capacity"));
        Assert.Contains(metrics, metric => metric.Name == "dotnet.process.cpu.time");
        Assert.Contains(metrics, metric => metric.Name == "dotnet.process.memory.working_set");
        Assert.Contains(metrics, metric => metric.Name == "dotnet.thread_pool.queue.length");
        Assert.Contains(metrics, metric => metric.Name == "dotnet.gc.heap.total_allocated");
        foreach (var scope in new[] { workload, queue, pacing, http })
        {
            scope.Dispose();
            scope.Dispose();
        }
        Assert.True(provider.ForceFlush());
        foreach (var name in new[] { "harness.workloads.active", "harness.requests.queued",
                     "harness.requests.pacing", "harness.requests.in_flight" })
        {
            Assert.Equal(0, Gauge(metrics, name));
        }
    }

    [Fact]
    public async Task Controller_gauges_read_live_engine_state()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMessagePipe();
        services.AddSingleton<DFrameControllerOptions>();
        services.AddSingleton<DFrame.Controller.IExecutionResultHistoryProvider, DFrame.Controller.InMemoryExecutionResultHistoryProvider>();
        services.AddSingleton<DFrameControllerExecutionEngine>();
        using var serviceProvider = services.BuildServiceProvider();
        var engine = serviceProvider.GetRequiredService<DFrameControllerExecutionEngine>();
        using var controller = new ControllerHarnessTelemetry(engine);
        var metrics = new List<Metric>();
        using var provider = Sdk.CreateMeterProviderBuilder().AddMeter(HarnessTelemetry.MeterName)
            .AddInMemoryExporter(metrics).Build();
        await controller.StartAsync(default);
        Assert.True(provider.ForceFlush());
        Assert.Equal(engine.CurrentConnectingCount, Gauge(metrics, "harness.workers.connected"));
        Assert.Equal(engine.IsRunning ? 1 : 0, Gauge(metrics, "harness.execution.running"));
        await controller.StopAsync(default);
    }

    [Fact]
    public async Task Workload_restores_gauges_after_HTTP_failure_and_queue_or_pacing_cancellation()
    {
        HarnessTelemetry.Initialize();
        var metrics = new List<Metric>();
        using var provider = Sdk.CreateMeterProviderBuilder().AddMeter(HarnessTelemetry.MeterName)
            .AddInMemoryExporter(metrics).Build();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new BlockingHandler(entered, release));
        using var canceledQueue = new CancellationTokenSource();
        using var canceledPacing = new CancellationTokenSource();
        var first = CreateWorkload(client);
        var queued = CreateWorkload(client);
        var paced = CreateWorkload(client);
        var execution = ExecutionId.New();
        var firstContext = new WorkloadContext(default, execution, 2, 0, 1, 2, default);
        var queueContext = new WorkloadContext(default, execution, 2, 1, 1, 2, canceledQueue.Token);
        var pacingContext = new WorkloadContext(default, ExecutionId.New(), 1, 0, 1, 1, canceledPacing.Token);
        await first.SetupAsync(firstContext);
        await queued.SetupAsync(queueContext);
        await paced.SetupAsync(pacingContext);
        Task? sending = null;
        Task? waiting = null;
        Task? pacing = null;
        try
        {
            sending = first.ExecuteAsync(firstContext);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(provider.ForceFlush());
            Assert.Equal(3, Gauge(metrics, "harness.workloads.active"));
            Assert.Equal(1, Gauge(metrics, "harness.requests.in_flight"));
            waiting = queued.ExecuteAsync(queueContext);
            Assert.True(provider.ForceFlush());
            Assert.Equal(1, Gauge(metrics, "harness.requests.queued"));
            canceledQueue.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            release.TrySetResult();
            await Assert.ThrowsAsync<HttpRequestException>(() => sending);
            pacing = paced.ExecuteAsync(pacingContext);
            Assert.True(provider.ForceFlush());
            Assert.Equal(1, Gauge(metrics, "harness.requests.pacing"));
            canceledPacing.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pacing);
            Assert.True(provider.ForceFlush());
            Assert.Equal(0, Gauge(metrics, "harness.requests.queued"));
            Assert.Equal(0, Gauge(metrics, "harness.requests.pacing"));
            Assert.Equal(0, Gauge(metrics, "harness.requests.in_flight"));
            var phases = new HashSet<string>();
            foreach (ref readonly var point in metrics.Last(metric => metric.Name == "harness.wait.duration").GetMetricPoints())
            {
                Assert.True(point.GetHistogramCount() > 0);
                Assert.True(point.GetHistogramSum() >= 0);
                foreach (var tag in point.Tags)
                {
                    if (tag.Key == "phase") phases.Add((string)tag.Value!);
                }
            }
            Assert.True(phases.SetEquals(["queue", "pacing"]));
        }
        finally
        {
            canceledQueue.Cancel();
            canceledPacing.Cancel();
            release.TrySetResult();
            foreach (var task in new[] { sending, waiting, pacing }.OfType<Task>())
            {
                await Record.ExceptionAsync(() => task);
            }
            await first.TeardownAsync(firstContext);
            await queued.TeardownAsync(queueContext);
            await paced.TeardownAsync(pacingContext);
            await first.TeardownAsync(firstContext);
        }
        Assert.True(provider.ForceFlush());
        Assert.Equal(0, Gauge(metrics, "harness.workloads.active"));
    }

    private static WebsiteBrowseWorkload CreateWorkload(HttpClient client) => new(
        client, NullLogger<WebsiteBrowseWorkload>.Instance,
        new WebsiteReportWriter(Path.GetTempPath(), enabled: false));

    private static long Gauge(List<Metric> metrics, string name)
    {
        foreach (ref readonly var point in metrics.Last(metric => metric.Name == name).GetMetricPoints())
        {
            return point.GetGaugeLastValueLong();
        }
        throw new InvalidOperationException($"No measurement for {name}.");
    }

    private sealed class BlockingHandler(TaskCompletionSource entered, TaskCompletionSource release) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            throw new HttpRequestException("Simulated HTTP failure", null, HttpStatusCode.ServiceUnavailable);
        }
    }
}
