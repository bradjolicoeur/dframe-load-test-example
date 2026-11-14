using System.Diagnostics;
using System.Diagnostics.Metrics;
using DFrame.Controller;

public static class HarnessTelemetry
{
    public const string MeterName = "DFrame.Harness";
    private static readonly Meter Meter = new(MeterName);
    private static readonly long Started = Stopwatch.GetTimestamp();
    private static long _workloads;
    private static long _queued;
    private static long _pacing;
    private static long _inFlight;
    private static readonly Histogram<double> Delay = Meter.CreateHistogram<double>("harness.wait.duration", "s");

    static HarnessTelemetry()
    {
        Meter.CreateObservableGauge("harness.workloads.active", () => Volatile.Read(ref _workloads), "{workload}");
        Meter.CreateObservableGauge("harness.requests.queued", () => Volatile.Read(ref _queued), "{request}");
        Meter.CreateObservableGauge("harness.requests.pacing", () => Volatile.Read(ref _pacing), "{request}");
        Meter.CreateObservableGauge("harness.requests.in_flight", () => Volatile.Read(ref _inFlight), "{request}");
        Meter.CreateObservableGauge("harness.process.cpu.capacity", () => Environment.ProcessorCount, "{cpu}");
        Meter.CreateObservableGauge("harness.process.uptime", () => Stopwatch.GetElapsedTime(Started).TotalSeconds, "s");
    }

    public static void Initialize() { }

    public static IDisposable EnterWorkload()
    {
        Interlocked.Increment(ref _workloads);
        return new Lease(() => Interlocked.Decrement(ref _workloads));
    }

    public static IDisposable EnterQueue()
    {
        Interlocked.Increment(ref _queued);
        return new Lease(() => Interlocked.Decrement(ref _queued));
    }

    public static IDisposable EnterPacing()
    {
        Interlocked.Increment(ref _pacing);
        return new Lease(() => Interlocked.Decrement(ref _pacing));
    }

    public static IDisposable EnterHttp()
    {
        Interlocked.Increment(ref _inFlight);
        return new Lease(() => Interlocked.Decrement(ref _inFlight));
    }

    public static void RecordWait(string phase, long started) =>
        Delay.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("phase", phase));

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

public sealed class ControllerHarnessTelemetry(DFrameControllerExecutionEngine engine) : IHostedService, IDisposable
{
    private readonly Meter _meter = new(HarnessTelemetry.MeterName);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _meter.CreateObservableGauge("harness.workers.connected", () => engine.CurrentConnectingCount, "{worker}");
        _meter.CreateObservableGauge("harness.execution.running", () => engine.IsRunning ? 1 : 0);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _meter.Dispose();
}
