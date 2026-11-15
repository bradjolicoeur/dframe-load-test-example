# DFrame website load-test example

An instrumented HTTP load test for [bradjolicoeur.com](https://bradjolicoeur.com/) using [DFrame](https://github.com/Cysharp/DFrame). Five local virtual workers cycle through Homepage, Work With Me, Blog, and Resume. OpenTelemetry emits live metrics, request traces, and structured logs.

## Run with Aspire, Prometheus, and Grafana

Requirements: **.NET 10 SDK** and a running **Docker-compatible container runtime** accessible to Aspire. The AppHost starts the Collector, Prometheus, and Grafana containers plus the DFrame application; it does not start a website test automatically.

Stop any previously running DFrame process with Ctrl+C to free ports 7312/7313, then run from the repository root:

```sh
dotnet run --project DFrameLoadTest.AppHost
```

Use the authenticated **Aspire dashboard URL printed in the terminal** (normally port 15000). It lists resource status, logs, metrics, and traces. Open the **dframe dashboard** endpoint or [http://localhost:7312](http://localhost:7312/) to control the test.

Select `WebsiteBrowseWorkload`, **Request** mode, **Total Request 20**, **Concurrency 1**, **Worker Limit 5**. Optionally enter `reportGroup`, such as `portfolio-repeat-01`, to label related runs/Repeat batches (1–80 ASCII letters, digits, or hyphens). Each worker executes four requests, potentially in parallel.

Open [Grafana → DFrame website](http://localhost:3000/d/dframe-website/dframe-website) for charts, or [Prometheus](http://localhost:9090/) for PromQL queries. In Grafana select a **Run**, **Repeat group**, and **Page** (or All). Run IDs appear in DFrame's terminal and result details. The provisioned dashboard includes request totals, failures, request rate, average/p95 HTTP latency, connection-pool wait, and page/status/outcome counts.

The export interval and scrape interval are both five seconds; allow roughly 10-15 seconds for points to appear. Rate charts need at least two scrape samples and can be empty for very short runs. Cumulative totals work for short runs, but a resource with no samples may show **No data**, not zero. Grafana variable options refresh on dashboard load; refresh the dashboard after a new run starts if necessary.

```text
DFrame worker host -- OTLP metrics/traces/logs --> Aspire dashboard
                   -- OTLP metrics --> Collector --> Prometheus --> Grafana
```

Aspire manages the lifecycle and supplies OTLP endpoints/authentication to the application. Only the worker host registers telemetry providers, preventing duplicate observations from DFrame's controller host. Prometheus stores time series in a named volume; Grafana configuration is provisioned from `observability/`. Prometheus defaults to 15 days of retention. Aspire's traces/logs are for local inspection, not durable trace/log storage. Stop AppHost with Ctrl+C to stop the resources; named data volumes are retained.

This is a **local development** stack. Grafana uses anonymous read-only access. Do not expose these HTTP endpoints publicly; a shared deployment needs authentication, TLS, and secret management.

## Harness health: distinguish the generator from the website

The same Grafana dashboard has a **Harness health** section. It measures the DFrame process, not the website server or the whole machine. Five virtual workers, the controller, logs, and telemetry all share that process. **Connected workers** comes from the controller's actual live connection count, not the configured value of five or the worker limit selected for a test.

| Signal | What it helps diagnose |
|---|---|
| Connected workers, running execution (0/1), active workloads | Missing worker connections versus workloads created by the current batch; active workloads remain counted until teardown |
| In-flight HTTP requests and connection-pool wait | Number of active calls and time waiting for an HTTP connection |
| Process CPU and working-set memory | Generator resource pressure; CPU combines user/system time and is normalized to available logical processors |
| Managed heap, allocation rate, GC pause percentage | Allocation pressure and pauses that can distort measured HTTP latency |
| Thread-pool thread count and queued work | Blocking/starvation inside the harness |
| Process telemetry uptime | Check that the producer is still reporting; a flat uptime with stale gauges can mean it stopped |

Harness gauges/histograms use the `DFrame.Harness` meter. Built-in CPU, memory, GC, thread-pool, exception, and lock-contention instruments come from **`System.Runtime`**, without an extra instrumentation package. Built-in **`System.Net.Http`** and **`System.Net.NameResolution`** metrics are also exported: inspect HTTP connection-pool wait time, open connections, and DNS lookup duration in Aspire. Filter network metrics to `server.address=bradjolicoeur.com` where available; the process also makes OTLP/network calls unrelated to the website.

Select a **Run** and **Harness process** in Grafana. Harness panels ignore page/group filters because runtime pressure and worker connections belong to the entire process. The run selector includes idle harnesses even before the first website request. Metric resources carry `test.run`, so runtime metrics can be correlated with the same run as request metrics.

**Interpretation:** rising HTTP latency while CPU, GC pauses, and thread-pool queues remain low points toward the website **or the network/client connection path**, not proof of a server problem. Rising harness CPU/GC pauses or sustained thread-pool queue growth means the generator may be distorting timings. To confirm a server bottleneck, compare with server-side CPU, request latency, and error telemetry; the harness cannot observe those remotely.

Five-second gauge samples may miss brief worker activity. Rate-based CPU/GC/allocation charts need multiple scrapes. Managed heap size reflects the last GC. Uptime starts when harness telemetry initializes. The Collector can retain a stopped producer's last values for 30 minutes: check that uptime is advancing and the Aspire resource is running before treating gauges as live. Prometheus target `up` verifies the Collector scrape, not the health of an individual DFrame process.

## Metric aggregation and timing

Metrics use the `DFrame.Website` meter:

| Instrument | Type / unit | Meaning |
|---|---|---|
| `website.requests` | Counter / requests | HTTP attempts that reached the send step, labeled with outcome/status |
| `website.http.duration` | Histogram / seconds | Send through body consumption and validation; failures may end earlier |
| `website.headers.duration` | Histogram / seconds | Send through response headers; absent if no headers arrived |
| `website.response.size` | Histogram / bytes | Consumed HTML bytes; zero when no body was consumed |

Request metrics carry `test.run`, `test.group`, `page`, `step`, `outcome`, and `http.response.status_code` (`none` before headers). **Execution/workload IDs and request sequences are trace/log fields, not metric labels**, avoiding a new time series per batch or virtual user. The fixed page set and finite outcomes keep other dimensions bounded. Use a sensible number of run/group names; these identifiers still create new series over time.

By default, one app launch has one run ID across all workers and Repeat batches. Restarting creates a new run ID. Set `LOAD_TEST_RUN_ID` before starting AppHost to choose a name deliberately. Reusing an ID intentionally includes earlier launches. All batches with the same `reportGroup` label aggregate in telemetry. An empty group is labeled `default`.

The dashboard sums counters and merges histogram buckets across workload/worker dimensions **before** calculating averages or percentiles. Histogram percentiles are bucket estimates. Small samples are not statistically robust p95/p99 evidence.

Prometheus names normalize dots to underscores and add unit/type suffixes. Example queries:

```promql
# Cumulative HTTP requests for a selected run, across workers and Repeat batches
sum(website_requests_total{test_run="YOUR_RUN_ID"})

# Request-weighted average HTTP duration per page since process start
sum by (page) (website_http_duration_seconds_sum)
/
sum by (page) (website_http_duration_seconds_count)

# Rolling p95, merged across workers before estimating
histogram_quantile(0.95,
  sum by (le, page) (rate(website_http_duration_seconds_bucket[1m])))
```

The dashboard's stat totals are **cumulative since each selected producer started**, not counts constrained to Grafana's selected time range. Rate charts are time-windowed. HTTP failure/cancellation timings remain included and are distinguishable by outcome. Cancellation before an attempt begins is not counted.

Metric aggregation also supports separate processes: send their metrics to the same Collector and use a shared `LOAD_TEST_RUN_ID`/group when desired. Resource instance IDs distinguish producers; sum their counters/buckets rather than selecting one producer or averaging percentiles. This example starts five *virtual* workers in one OS process, not five independent processes.

## Traces and logs

In Aspire, select **Traces** to inspect a span per HTTP attempt, including page, outcome, status, execution/workload IDs, sequence, HTTP duration, byte count, and errors. Select **Structured logs** for request start/completion and exception details; logs emitted during a request are correlated with its span.

Checks require a successful HTTP status, `text/html`, and a stable page-specific marker. Only HTML requests are made: no scripts, images, third-party resources, browser rendering, or contact form submissions. Update content markers in `WebsiteBrowseWorkload.cs` if site content changes.

## Run without AppHost / use another OTLP backend

```sh
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 \
OTEL_METRIC_EXPORT_INTERVAL=5000 \
dotnet run --project DFrameLoadTest
```

The standard OTLP exporter defaults to gRPC on port 4317 for metrics, traces, and logs. Standard `OTEL_EXPORTER_OTLP_*` settings (endpoint, protocol, headers, signal-specific overrides) can point it at another compatible backend. Set `PROMETHEUS_OTLP_ENDPOINT` to a second **gRPC base endpoint** to export metrics to a Collector as well; AppHost sets this automatically to its Collector. Avoid pointing both exporters at the same metrics backend, which would duplicate telemetry.

Without a listening receiver, delivery will not work; the DFrame UI alone is not a telemetry backend. OTLP export is asynchronous and best-effort, not an audit log. Inspect resource logs and Prometheus target health if charts are empty; stop gracefully to flush exporters. The worker host keeps exporting while idle between Repeat batches.

The DFrame result drawer shows request counts and telemetry run/group IDs. Static report generation and conversion commands have been removed; existing files are left untouched and are not imported into telemetry.

## Traffic and load

The HTTPS host and paths are fixed and redirects are disabled. There is **no application-level pacing, request cap, or one-connection limit**: DFrame workers and concurrency can send requests in parallel as fast as they complete. The 20-second HTTP timeout still applies. OS, network, HTTP protocol, server, and telemetry overhead can limit throughput.

Repeat increases requests and/or worker limits between completed batches; it does not provide a fixed time-based requests-per-second ramp. Duration runs at the selected concurrency for the requested period; Infinite continues until stopped. Increase load deliberately and only test systems you own or are authorized to test. Per-request logs and traces add overhead under high load.

## Tests

```sh
dotnet test tests/DFrameLoadTest.Tests/DFrameLoadTest.Tests.csproj
```

Tests use fake HTTP, in-memory telemetry export, and a loopback OTLP receiver; they send no website traffic. They cover metrics aggregation, OTLP export, parallel requests and lifecycle cleanup, runtime metrics, correlated spans, page validation, and failures/cancellation.

For an optional Collector integration check, set `OTEL_TEST_COLLECTOR_ENDPOINT` to a running Collector's OTLP HTTP base URL (for example `http://localhost:4318`); the wire-export test forwards its synthetic metrics there. The AppHost exposes only the Collector's gRPC receiver, so this optional check requires a separately accessible HTTP receiver.
