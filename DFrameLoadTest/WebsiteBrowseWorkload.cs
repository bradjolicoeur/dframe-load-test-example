using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DFrame;

public sealed class WebsiteBrowseWorkload(
    HttpClient httpClient,
    ILogger<WebsiteBrowseWorkload> logger,
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

    private static readonly ConcurrentDictionary<string, RunBudget> Runs = new();
    private RunBudget? _budget;
    private long _requestCount;
    private IDisposable? _activeWorkload;

    public override Task SetupAsync(WorkloadContext context)
    {
        if (!string.IsNullOrWhiteSpace(reportGroup)
            && (reportGroup.Length > 80 || reportGroup.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
        {
            throw new ArgumentException("Report group must be 1-80 ASCII letters, digits, or hyphens.");
        }
        _budget = Runs.GetOrAdd(context.ExecutionId.ToString(), static _ => new RunBudget());
        Interlocked.Increment(ref _budget.WorkloadsRegistered);
        logger.LogInformation(
            "Starting execution {ExecutionId}, workload {WorkloadId}. OTEL run {RunId}, group {ReportGroup}",
            context.ExecutionId, context.WorkloadId, WebsiteTelemetry.RunId, reportGroup);
        _activeWorkload = HarnessTelemetry.EnterWorkload();
        return Task.CompletedTask;
    }

    public override async Task ExecuteAsync(WorkloadContext context)
    {
        var budget = _budget ?? throw new InvalidOperationException("Run SetupAsync before executing.");
        context.CancellationToken.ThrowIfCancellationRequested();
        var sequence = Interlocked.Increment(ref budget.RequestsStarted);
        var step = Steps[(int)((sequence - 1) % Steps.Length)];
        var started = Stopwatch.GetTimestamp();
        int? statusCode = null;
        double? headersMilliseconds = null;
        var responseBytes = 0;
        var outcome = "Succeeded";
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
            activity?.SetStatus(ActivityStatusCode.Error, $"{exception.GetType().Name}: {exception.Message}");
            activity?.SetTag("error.type", exception.GetType().FullName);
            logger.LogError(exception,
                "Execution {ExecutionId}, request {Sequence}, step {Step}: {Outcome}",
                context.ExecutionId, sequence, step.Name, outcome);
            throw;
        }
        finally
        {
            var httpMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var result = new WebsiteRequestResult(
                step.Name, step.Path, statusCode, headersMilliseconds, httpMilliseconds, responseBytes, outcome);
            WebsiteTelemetry.Record(result, reportGroup);
            activity?.SetTag("http.response.status_code", statusCode);
            activity?.SetTag("test.outcome", outcome);
            activity?.SetTag("test.http.duration_ms", httpMilliseconds);
            activity?.SetTag("http.response.body.size", responseBytes);
            _requestCount++;
            logger.LogInformation(
                "Execution {ExecutionId}, request {Sequence}, step {Step}: {Outcome}, HTTP {StatusCode}, " +
                "{HttpMilliseconds:F2} ms HTTP, {ResponseBytes} bytes",
                context.ExecutionId, sequence, step.Name, outcome, statusCode,
                httpMilliseconds, responseBytes);
        }
    }

    public override Dictionary<string, string>? Complete(WorkloadContext context)
    {
        var details = new Dictionary<string, string>
        {
            ["OpenTelemetry run ID"] = WebsiteTelemetry.RunId,
            ["OpenTelemetry group"] = string.IsNullOrWhiteSpace(reportGroup) ? "default" : reportGroup,
            ["Live dashboard"] = "Aspire dashboard: Metrics, Traces, and Structured logs for dframe",
            ["HTTP requests sent"] = _requestCount.ToString(CultureInfo.InvariantCulture),
            ["Timing scope"] = "HTTP timing includes connection-pool wait, response consumption and validation."
        };
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

    private sealed class RunBudget
    {
        public long RequestsStarted;
        public int WorkloadsTornDown;
        public int WorkloadsRegistered;
    }
}
