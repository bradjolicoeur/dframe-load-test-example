using DFrame;
using Microsoft.Extensions.DependencyInjection;

if (args.Length > 0 && args[0] is "--render-report" or "--aggregate-report" or "--aggregate-group")
{
    throw new ArgumentException("Static report generation and conversion have been removed. Use OpenTelemetry dashboards instead.");
}

var builder = DFrameApp.CreateBuilder(7312, 7313);
builder.ControllerBuilder.WebHost.UseStaticWebAssets();
builder.ControllerBuilder.Services.AddHostedService<ControllerHarnessTelemetry>();
builder.ConfigureWorker(options => options.VirtualProcess = 5);
builder.WorkerBuilder.ConfigureServices(services => services.AddWebsiteTelemetry());
builder.WorkerBuilder.ConfigureLogging(logging => logging.AddWebsiteTelemetry());
builder.ConfigureServices(services =>
{
    services.AddSingleton(new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    });
});

Console.WriteLine($"OpenTelemetry run ID: {WebsiteTelemetry.RunId}. Use the Aspire dashboard for live telemetry.");
await builder.RunAsync();
