var builder = DistributedApplication.CreateBuilder(args);
var observability = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "observability"));

var collector = builder.AddContainer("otel-collector", "otel/opentelemetry-collector-contrib", "0.139.0")
    .WithContainerName("dframe-otel-collector")
    .WithBindMount(Path.Combine(observability, "collector.yaml"), "/etc/otelcol-contrib/config.yaml", isReadOnly: true)
    .WithEndpoint(port: 14317, targetPort: 4317, scheme: "http", name: "otlp", isProxied: false)
    .WithHttpEndpoint(port: 18888, targetPort: 8889, name: "metrics", isProxied: false);

var prometheus = builder.AddContainer("prometheus", "prom/prometheus", "v3.7.3")
    .WithContainerName("dframe-prometheus")
    .WithBindMount(Path.Combine(observability, "prometheus.yaml"), "/etc/prometheus/prometheus.yml", isReadOnly: true)
    .WithVolume("dframe-prometheus-data", "/prometheus")
    .WithHttpEndpoint(port: 9090, targetPort: 9090, name: "http", isProxied: false)
    .WaitFor(collector);

builder.AddContainer("grafana", "grafana/grafana", "12.3.0")
    .WithBindMount(Path.Combine(observability, "grafana", "provisioning"), "/etc/grafana/provisioning", isReadOnly: true)
    .WithBindMount(Path.Combine(observability, "grafana", "dashboards"), "/var/lib/grafana/dashboards", isReadOnly: true)
    .WithVolume("dframe-grafana-data", "/var/lib/grafana")
    .WithHttpEndpoint(port: 3000, targetPort: 3000, name: "http", isProxied: false)
    .WithEnvironment("GF_AUTH_ANONYMOUS_ENABLED", "true")
    .WithEnvironment("GF_AUTH_ANONYMOUS_ORG_ROLE", "Viewer")
    .WithEnvironment("GF_AUTH_DISABLE_LOGIN_FORM", "true")
    .WaitFor(prometheus);

builder.AddProject<Projects.DFrameLoadTest>("dframe", launchProfileName: null)
    .WithHttpEndpoint(port: 7312, targetPort: 7312, name: "dashboard", isProxied: false)
    .WithHttpEndpoint(port: 7313, targetPort: 7313, name: "workers", isProxied: false)
    .WithEnvironment("LOAD_TEST_RUN_ID",
        Environment.GetEnvironmentVariable("LOAD_TEST_RUN_ID") ?? Guid.NewGuid().ToString("N"))
    .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", "5000")
    .WithEnvironment("PROMETHEUS_OTLP_ENDPOINT", collector.GetEndpoint("otlp"))
    .WaitFor(collector);

builder.Build().Run();
