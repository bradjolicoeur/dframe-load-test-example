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
        using var http = HarnessTelemetry.EnterHttp();
        Assert.True(provider.ForceFlush());
        Assert.Equal(1, Gauge(metrics, "harness.workloads.active"));
        Assert.Equal(1, Gauge(metrics, "harness.requests.in_flight"));
        Assert.Equal(Environment.ProcessorCount, Gauge(metrics, "harness.process.cpu.capacity"));
        Assert.Contains(metrics, metric => metric.Name == "dotnet.process.cpu.time");
        Assert.Contains(metrics, metric => metric.Name == "dotnet.process.memory.working_set");
        Assert.Contains(metrics, metric => metric.Name == "dotnet.thread_pool.queue.length");
        Assert.Contains(metrics, metric => metric.Name == "dotnet.gc.heap.total_allocated");
        foreach (var scope in new[] { workload, http })
        {
            scope.Dispose();
            scope.Dispose();
        }
        Assert.True(provider.ForceFlush());
        foreach (var name in new[] { "harness.workloads.active", "harness.requests.in_flight" })
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
    public async Task Workloads_run_in_parallel_and_restore_gauges_after_HTTP_failure_and_cancellation()
    {
        HarnessTelemetry.Initialize();
        var metrics = new List<Metric>();
        using var provider = Sdk.CreateMeterProviderBuilder().AddMeter(HarnessTelemetry.MeterName)
            .AddInMemoryExporter(metrics).Build();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new BlockingHandler(entered, release));
        using var cancellation = new CancellationTokenSource();
        var first = CreateWorkload(client);
        var second = CreateWorkload(client);
        var execution = ExecutionId.New();
        var firstContext = new WorkloadContext(default, execution, 2, 0, 1, 2, default);
        var secondContext = new WorkloadContext(default, execution, 2, 1, 1, 2, cancellation.Token);
        await first.SetupAsync(firstContext);
        await second.SetupAsync(secondContext);
        Task? sending = null;
        Task? other = null;
        try
        {
            sending = first.ExecuteAsync(firstContext);
            other = second.ExecuteAsync(secondContext);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(provider.ForceFlush());
            Assert.Equal(2, Gauge(metrics, "harness.workloads.active"));
            Assert.Equal(2, Gauge(metrics, "harness.requests.in_flight"));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => other);
            release.TrySetResult();
            await Assert.ThrowsAsync<HttpRequestException>(() => sending);
            Assert.True(provider.ForceFlush());
            Assert.Equal(0, Gauge(metrics, "harness.requests.in_flight"));
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            foreach (var task in new[] { sending, other }.OfType<Task>())
            {
                await Record.ExceptionAsync(() => task);
            }
            await first.TeardownAsync(firstContext);
            await second.TeardownAsync(secondContext);
            await first.TeardownAsync(firstContext);
        }
        Assert.True(provider.ForceFlush());
        Assert.Equal(0, Gauge(metrics, "harness.workloads.active"));
    }

    private static WebsiteBrowseWorkload CreateWorkload(HttpClient client) => new(
        client, NullLogger<WebsiteBrowseWorkload>.Instance);

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
        private int _count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == 2) entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            throw new HttpRequestException("Simulated HTTP failure", null, HttpStatusCode.ServiceUnavailable);
        }
    }
}
