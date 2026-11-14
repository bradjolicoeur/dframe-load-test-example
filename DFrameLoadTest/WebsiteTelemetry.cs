using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using OpenTelemetry.Exporter;

public static class WebsiteTelemetry
{
    public const string MeterName = "DFrame.Website";
    public static readonly ActivitySource Activities = new(MeterName);
    public static readonly string RunId =
        Environment.GetEnvironmentVariable("LOAD_TEST_RUN_ID") ?? Guid.NewGuid().ToString("N");

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("website.requests", "{request}");
    private static readonly Counter<long> Rejections = Meter.CreateCounter<long>("website.rejected", "{call}");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("website.http.duration", "s");
    private static readonly Histogram<double> Headers = Meter.CreateHistogram<double>("website.headers.duration", "s");
    private static readonly Histogram<double> Wait = Meter.CreateHistogram<double>("website.wait.duration", "s");
    private static readonly Histogram<long> Bytes = Meter.CreateHistogram<long>("website.response.size", "By");

    public static IServiceCollection AddWebsiteTelemetry(this IServiceCollection services)
    {
        HarnessTelemetry.Initialize();
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddAttributes(CreateResource().Build().Attributes))
            .WithTracing(tracing => tracing.AddSource(MeterName).AddOtlpExporter())
            .WithMetrics(metrics =>
            {
                metrics
                .AddMeter(MeterName, HarnessTelemetry.MeterName, "System.Runtime", "System.Net.Http", "System.Net.NameResolution")
                .AddView("harness.wait.duration", new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [0.001, 0.01, 0.1, 0.5, 1, 2, 5, 10, 20, 30, 60]
                })
                .AddView("website.http.duration", new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 20, 30]
                })
                .AddView("website.headers.duration", new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 20, 30]
                })
                .AddView("website.wait.duration", new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [0.001, 0.01, 0.1, 0.5, 1, 2, 5, 10, 20, 30, 60]
                })
                .AddOtlpExporter();
                if (Environment.GetEnvironmentVariable("PROMETHEUS_OTLP_ENDPOINT") is { Length: > 0 } endpoint)
                {
                    metrics.AddOtlpExporter("prometheus", options =>
                    {
                        options.Endpoint = new Uri(endpoint);
                        options.Protocol = OtlpExportProtocol.Grpc;
                    });
                }
            });
        return services;
    }

    public static ILoggingBuilder AddWebsiteTelemetry(this ILoggingBuilder logging)
    {
        logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.SetResourceBuilder(CreateResource());
            options.AddOtlpExporter();
        });
        return logging;
    }

    public static void Record(WebsiteRequestResult request, string? reportGroup)
    {
        var tags = new TagList
        {
            { "test.run", RunId },
            { "test.group", Group(reportGroup) },
            { "page", request.Path },
            { "step", request.Step },
            { "outcome", request.Outcome },
            { "http.response.status_code", request.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none" }
        };
        Requests.Add(1, tags);
        Duration.Record(request.HttpMilliseconds / 1000, tags);
        Wait.Record(request.WaitMilliseconds / 1000, tags);
        Bytes.Record(request.ResponseBytes, tags);
        if (request.HeadersMilliseconds is { } headers)
        {
            Headers.Record(headers / 1000, tags);
        }
    }

    public static void RecordRejection(string? reportGroup) =>
        Rejections.Add(1, new TagList
        {
            { "test.run", RunId }, { "test.group", Group(reportGroup) }, { "reason", "request_cap" }
        });

    private static string Group(string? reportGroup) =>
        string.IsNullOrWhiteSpace(reportGroup) ? "default" : reportGroup;

    private static ResourceBuilder CreateResource() => ResourceBuilder.CreateEmpty()
        .AddAttributes([new KeyValuePair<string, object>("test.run", RunId)])
        .AddService("DFrameLoadTest",
            serviceInstanceId: Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .AddEnvironmentVariableDetector();
}
