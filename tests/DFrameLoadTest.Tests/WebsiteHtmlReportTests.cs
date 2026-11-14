using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DFrameLoadTest.Tests;

public sealed class WebsiteHtmlReportTests
{
    [Fact]
    public void Renders_offline_charts_and_escaped_request_details_with_invariant_numbers()
    {
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var report = CreateReport();
            var html = WebsiteHtmlReport.Render(report);
            Assert.Contains("Request latency timeline", html);
            Assert.Contains("Per-page HTTP latency", html);
            Assert.Contains("Timing breakdown", html);
            Assert.Contains("Outcomes by page", html);
            Assert.Contains("Completion callback received", html);
            Assert.Contains("3 HTTP requests", html);
            Assert.Contains("2 cap-rejected calls", html);
            Assert.Contains("100.50 ms", html);
            Assert.Contains("HTTP timing includes response consumption", html);
            Assert.Contains("headers", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("&lt;script&gt;bad&lt;/script&gt;", html);
            Assert.DoesNotContain("<script>", html);
            Assert.DoesNotContain("<script ", html);
            Assert.DoesNotContain("<link ", html);
            Assert.DoesNotContain("<img ", html);
            var charts = Regex.Matches(html, "<svg\\b.*?</svg>", RegexOptions.Singleline)
                .Select(match => XDocument.Parse(match.Value)).ToArray();
            Assert.Equal(4, charts.Length);
            XNamespace svg = "http://www.w3.org/2000/svg";
            var points = charts[0].Descendants(svg + "circle").ToArray();
            Assert.Equal(5, points.Length);
            Assert.Equal(2, points.Count(point => (string?)point.Attribute("stroke") == "#dc2626"));
            foreach (var chart in charts)
            {
                Assert.NotNull(chart.Root!.Element(svg + "title"));
                foreach (var attribute in chart.Descendants().Attributes()
                             .Where(attribute => new[] { "x", "y", "cx", "cy", "width", "height" }
                                 .Contains(attribute.Name.LocalName)))
                {
                    var value = double.Parse(attribute.Value, CultureInfo.InvariantCulture);
                    Assert.True(double.IsFinite(value));
                    Assert.True(value >= 0);
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
        }
    }

    [Fact]
    public void Empty_partial_run_has_no_fake_latency_or_invalid_chart_coordinates()
    {
        var report = new WebsiteRunReport("execution", "workload", 1, false, 0,
            [WebsitePageSummary.Create("Blog", "/Blog", [])], []);
        var html = WebsiteHtmlReport.Render(report);
        Assert.Contains("Partial run snapshot", html);
        Assert.Contains("No HTTP requests recorded yet.", html);
        Assert.Contains("No requests for this page.", html);
        Assert.DoesNotContain("NaN", html);
        Assert.DoesNotContain("Infinity", html);
    }

    [Fact]
    public void Writer_updates_html_next_to_json_and_supports_existing_json_without_rewriting_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dframe-html-tests-{Guid.NewGuid():N}");
        try
        {
            var writer = new WebsiteReportWriter(directory);
            var report = CreateReport() with { Completed = false };
            writer.Write("run.json", report);
            var path = writer.GetHtmlPath("run.json");
            Assert.Equal(Path.Combine(directory, "run.html"), path);
            Assert.Contains("Partial run snapshot", File.ReadAllText(path));
            report = report with { Completed = true };
            writer.Write("run.json", report);
            Assert.Contains("Completion callback received", File.ReadAllText(path));
            var json = File.ReadAllText(writer.GetPath("run.json"));
            File.Delete(path);
            var restored = JsonSerializer.Deserialize<WebsiteRunReport>(
                json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            writer.WriteHtml("run.json", restored);
            Assert.Equal(WebsiteHtmlReport.Render(restored), File.ReadAllText(path));
            Assert.Equal(json, File.ReadAllText(writer.GetPath("run.json")));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static WebsiteRunReport CreateReport()
    {
        WebsiteRequestResult[] requests =
        [
            new(1, "Homepage", "/", DateTimeOffset.UtcNow, 200, 80, 100.5, 0, 100, "Succeeded", null),
            new(2, "Homepage", "/", DateTimeOffset.UtcNow, 503, 90, 200, 1000, 0, "Failed", "<script>bad</script>"),
            new(3, "Homepage", "/", DateTimeOffset.UtcNow, null, null, 50, 1000, 0, "Canceled", "Canceled")
        ];
        return new WebsiteRunReport("d163fbfe-a694-4537-95b1-67fdb97b73db", "workload", 1, true, 2,
            [WebsitePageSummary.Create("Homepage", "/", requests)], requests);
    }
}
