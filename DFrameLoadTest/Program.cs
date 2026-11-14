using DFrame;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

if (args.Length > 0 && args[0] is "--render-report" or "--aggregate-report" or "--aggregate-group")
{
    if (args.Length != 2)
    {
        throw new ArgumentException("Usage: --render-report|--aggregate-report|--aggregate-group <path-to-json-report>");
    }
    var jsonPath = Path.GetFullPath(args[1]);
    var report = JsonSerializer.Deserialize<WebsiteRunReport>(
        await File.ReadAllTextAsync(jsonPath),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException("The JSON file does not contain a run report.");
    var writer = new WebsiteReportWriter(Path.GetDirectoryName(jsonPath)!);
    if (args[0] == "--aggregate-group")
    {
        Console.WriteLine($"Repeat group HTML charts: {writer.WriteGroup(report)}");
        return;
    }
    if (args[0] == "--aggregate-report")
    {
        Console.WriteLine($"Combined HTML charts: {writer.WriteCombined(report)}");
        return;
    }
    writer.WriteHtml(Path.GetFileName(jsonPath), report);
    Console.WriteLine($"HTML charts: {writer.GetHtmlPath(Path.GetFileName(jsonPath))}");
    return;
}

var builder = DFrameApp.CreateBuilder(7312, 7313);
builder.ControllerBuilder.WebHost.UseStaticWebAssets();
builder.ControllerBuilder.Services.AddHostedService<ControllerHarnessTelemetry>();
builder.ConfigureWorker(options => options.VirtualProcess = 5);
builder.WorkerBuilder.ConfigureServices(services => services.AddWebsiteTelemetry());
builder.WorkerBuilder.ConfigureLogging(logging => logging.AddWebsiteTelemetry());
builder.ConfigureServices(services =>
{
    services.AddSingleton(new WebsiteReportWriter(
        Path.Combine(builder.ControllerBuilder.Environment.ContentRootPath, "artifacts", "load-tests"),
        builder.ControllerBuilder.Configuration.GetValue<bool>("LOAD_TEST_STATIC_REPORTS")));
    services.AddSingleton(new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        MaxConnectionsPerServer = 1
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    });
});

Console.WriteLine($"OpenTelemetry run ID: {WebsiteTelemetry.RunId}. Use the Aspire dashboard for live telemetry.");
await builder.RunAsync();
