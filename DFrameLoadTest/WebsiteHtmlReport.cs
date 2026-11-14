using System.Globalization;
using System.Net;
using System.Text;

public static class WebsiteHtmlReport
{
    private static readonly string[] Colors = ["#2563eb", "#7c3aed", "#0891b2", "#b45309"];

    public static string Render(WebsiteRunReport report)
    {
        var requests = report.Requests.OrderBy(request => request.Sequence).ToArray();
        var timeline = new StringBuilder();
        var maximum = Math.Max(1, requests.Select(request => request.HttpMilliseconds).DefaultIfEmpty().Max());
        var lastSequence = Math.Max(1, requests.Select(request => request.Sequence).DefaultIfEmpty().Max());
        timeline.Append($"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 900 280" role="img" aria-label="HTTP latency by request sequence">
            <title>HTTP latency by request sequence in milliseconds</title>
            <line x1="65" y1="225" x2="865" y2="225" stroke="#64748b"/>
            <line x1="65" y1="30" x2="65" y2="225" stroke="#64748b"/>
            <text x="65" y="20">{Number(maximum)} ms</text>
            <text x="15" y="229">0 ms</text>
            <text x="65" y="250">1</text>
            <text x="845" y="250">{lastSequence}</text>
            <text x="360" y="272">Request sequence (not elapsed time)</text>
            """);
        foreach (var request in requests)
        {
            var x = lastSequence == 1 ? 465 : 65 + 800d * (request.Sequence - 1) / (lastSequence - 1);
            var y = 225 - 190 * request.HttpMilliseconds / maximum;
            var pageIndex = Array.FindIndex(report.Pages, page => page.Path == request.Path);
            var color = Colors[Math.Max(0, pageIndex) % Colors.Length];
            var title = Encode($"#{request.Sequence} {request.Step}: {Number(request.HttpMilliseconds)} ms, {request.Outcome}");
            timeline.Append(CultureInfo.InvariantCulture,
                $"<circle cx=\"{x:F2}\" cy=\"{y:F2}\" r=\"5\" fill=\"{color}\"><title>{title}</title></circle>");
            if (request.Outcome != "Succeeded")
            {
                timeline.Append(CultureInfo.InvariantCulture,
                    $"<circle cx=\"{x:F2}\" cy=\"{y:F2}\" r=\"9\" fill=\"none\" stroke=\"#dc2626\" stroke-width=\"3\"><title>{title}</title></circle>");
            }
        }
        timeline.Append("</svg>");

        var legend = string.Join(" ", report.Pages.Select((page, index) =>
            $"<span class=\"legend\"><i style=\"background:{Colors[index % Colors.Length]}\"></i>{Encode(page.Step)}</span>"));
        var pages = new StringBuilder();
        foreach (var page in report.Pages)
        {
            pages.Append($"<h3>{Encode(page.Step)} <small>{Encode(page.Path)}</small></h3>");
            if (page.Requests == 0)
            {
                pages.Append("<p>No requests for this page.</p>");
                continue;
            }
            pages.Append(Bars(
                ["Average", "p50", "p95", "Maximum"],
                [page.AverageMilliseconds ?? 0, page.P50Milliseconds ?? 0,
                    page.P95Milliseconds ?? 0, page.MaxMilliseconds ?? 0],
                ["#2563eb", "#0891b2", "#7c3aed", "#b45309"], maximum, "ms"));
        }

        var totalHttp = requests.Sum(request => request.HttpMilliseconds);
        var totalWait = requests.Sum(request => request.WaitMilliseconds);
        var breakdown = Bars(["HTTP", "Queue / pacing"], [totalHttp, totalWait],
            ["#2563eb", "#64748b"], Math.Max(1, Math.Max(totalHttp, totalWait)), "ms");
        var outcomes = new StringBuilder();
        foreach (var page in report.Pages)
        {
            outcomes.Append($"<h3>{Encode(page.Step)}</h3>");
            outcomes.Append(Bars(["Succeeded", "Failed", "Canceled"],
                [page.Succeeded, page.Failed, page.Canceled],
                ["#15803d", "#dc2626", "#b45309"], Math.Max(1, page.Requests), "requests"));
        }

        var rows = string.Join("", requests.Select(request => $"""
            <tr><td>{request.Sequence}</td><td>{Encode(request.StartedAt.ToString("O", CultureInfo.InvariantCulture))}</td>
            <td>{Encode(request.Step)}<br/><code>{Encode(request.Path)}</code></td>
            <td><code>{Encode(request.WorkloadId ?? report.WorkloadId)}</code></td>
            <td><code>{Encode(request.ExecutionId ?? report.ExecutionId)}</code><br/>#{request.OriginalSequence ?? request.Sequence}</td>
            <td>{Encode(request.Outcome)}</td><td>{request.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "N/A"}</td>
            <td>{Number(request.HeadersMilliseconds)}</td><td>{Number(request.HttpMilliseconds)}</td>
            <td>{Number(request.WaitMilliseconds)}</td><td>{request.ResponseBytes}</td><td>{Encode(request.Error)}</td></tr>
            """));

        var batches = new StringBuilder();
        if (report.Batches.Length > 0)
        {
            batches.Append("<section><h2>Repeat batches</h2><p>Only recorded batches are included. " +
                "More batches may still be scheduled; completed recorded batches do not mean Repeat has finished.</p>" +
                "<div class=\"scroll\"><table><thead><tr><th>Execution</th><th>State</th><th>Workloads recorded / expected</th>" +
                "<th>HTTP requests</th><th>Rejected</th><th>Per-page HTTP average / p95 (ms)</th></tr></thead><tbody>");
            foreach (var batch in report.Batches)
            {
                var timings = string.Join("<br/>", batch.Pages.Select(page =>
                    $"{Encode(page.Step)}: {Number(page.AverageMilliseconds)} / {Number(page.P95Milliseconds)}"));
                batches.Append($"<tr><td><code>{Encode(batch.ExecutionId)}</code></td>" +
                    $"<td>{(batch.Completed ? "Completed" : "Partial")}</td>" +
                    $"<td>{batch.WorkloadCount} / {batch.ExpectedWorkloadCount}</td>" +
                    $"<td>{batch.Requests}</td><td>{batch.RejectedCalls}</td><td>{timings}</td></tr>");
            }
            batches.Append("</tbody></table></div></section>");
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8"/>
            <meta name="viewport" content="width=device-width, initial-scale=1"/>
            <title>DFrame website run report</title>
            <style>
            body{font:16px system-ui,sans-serif;background:#f1f5f9;color:#0f172a;margin:0;padding:24px}
            main{max-width:1200px;margin:auto}section{background:white;border:1px solid #cbd5e1;border-radius:12px;padding:24px;margin:20px 0}
            h1,h2,h3{line-height:1.3}small,.note{color:#475569}code{overflow-wrap:anywhere}
            svg{display:block;width:100%;height:auto}svg text{font:14px system-ui,sans-serif;fill:#334155}
            .legend{display:inline-block;margin:8px 16px 8px 0}.legend i{display:inline-block;width:12px;height:12px;margin-right:6px}
            .scroll{overflow-x:auto}table{border-collapse:collapse;font-size:13px;width:100%}th,td{text-align:left;padding:10px;border-bottom:1px solid #cbd5e1;vertical-align:top}
            th{white-space:nowrap}td:last-child{min-width:180px;overflow-wrap:anywhere}
            @media print{body{background:white;padding:0}section{break-inside:avoid}.scroll{overflow:visible} }
            </style>
            </head>
            <body><main>
            <h1>DFrame website run report</h1>
            <p><strong>{{(report.Batches.Length > 0 ? (report.Completed ? "Recorded batches completed (group snapshot)" : "Partial group snapshot") : (report.Completed ? "Completion callback received" : "Partial run snapshot"))}}</strong> &middot;
            {{requests.Length}} HTTP requests &middot; {{report.RejectedCalls}} cap-rejected calls (no HTTP)</p>
            <p>Execution: <code>{{Encode(report.ExecutionId)}}</code><br/>
            Workload: <code>{{Encode(report.WorkloadId)}}</code> &middot; Worker process: {{report.WorkerProcessId}}</p>
            <p><strong>{{(report.Batches.Length > 0 ? $"Repeat group: {Encode(report.ReportGroup)}, {report.Batches.Length} recorded batches" : (report.IsAggregate ? $"Combined execution: {report.WorkloadCount} of {report.ExpectedWorkloadCount} workload instances" : "Individual workload instance"))}}</strong></p>
            <p class="note">{{(report.IsAggregate ? "All recorded concurrent workloads in this worker process. Percentiles are recalculated from individual requests." : "One workload instance only; use the combined report for totals across concurrency.")}}
            HTTP timing includes response consumption and validation, excludes queue/pacing.
            Failures can finish before body consumption. Percentiles use nearest rank; small samples are not capacity evidence.
            Completion does not imply all requests succeeded. Refresh this file to see newly saved data.</p>
            {{batches}}
            <section><h2>Request latency timeline</h2>{{legend}}
            <p class="note">Points are colored by page. A red ring marks failure or cancellation. Hover a point for details.</p>
            {{(requests.Length == 0 ? "<p>No HTTP requests recorded yet.</p>" : timeline.ToString())}}</section>
            <section><h2>Per-page HTTP latency</h2><p class="note">All bars share a scale; values include failed and canceled requests.</p>{{pages}}</section>
            <section><h2>Timing breakdown</h2><p class="note">Summed request timings, not wall-clock run duration. Instrumentation overhead is not included.</p>{{breakdown}}</section>
            <section><h2>Outcomes by page</h2>{{outcomes}}</section>
            <section><h2>Request details</h2><div class="scroll"><table>
            <thead><tr><th>Sequence</th><th>Started (UTC)</th><th>Step / path</th><th>Workload ID</th><th>Batch / original sequence</th><th>Outcome</th><th>HTTP status</th>
            <th>Headers ms</th><th>HTTP ms</th><th>Wait ms</th><th>Bytes</th><th>Error</th></tr></thead>
            <tbody>{{rows}}</tbody></table></div></section>
            </main></body></html>
            """;
    }

    private static string Bars(string[] labels, double[] values, string[] colors, double maximum, string unit)
    {
        var height = labels.Length * 32 + 10;
        var svg = new StringBuilder($"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 900 {height}" role="img" aria-label="Bar chart in {Encode(unit)}">
            <title>Bar chart in {Encode(unit)}</title>
            """);
        for (var index = 0; index < labels.Length; index++)
        {
            var y = 5 + index * 32;
            var width = 560 * values[index] / maximum;
            svg.Append(CultureInfo.InvariantCulture,
                $"""
                <text x="5" y="{y + 17}">{Encode(labels[index])}</text>
                <rect x="140" y="{y}" width="{width:F2}" height="23" fill="{colors[index]}"><title>{Encode(labels[index])}: {Number(values[index])} {Encode(unit)}</title></rect>
                <text x="715" y="{y + 17}">{Number(values[index])} {Encode(unit)}</text>
                """);
        }
        return svg.Append("</svg>").ToString();
    }

    private static string Number(double? value) => value?.ToString("F2", CultureInfo.InvariantCulture) ?? "N/A";
    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");
}
