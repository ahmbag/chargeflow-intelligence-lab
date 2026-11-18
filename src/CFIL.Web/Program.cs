using System.Net;
using CFIL.Infrastructure;
using CFIL.Observation;
using CFIL.Web.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders().AddConsole();
builder.WebHost.UseStaticWebAssets();
var root = new DirectoryInfo(builder.Environment.ContentRootPath);
while (root is not null && !File.Exists(Path.Combine(root.FullName,"ChargeFlowIntelligenceLab.sln"))) root=root.Parent;
if(root is null)throw new InvalidOperationException("Start inside the CFIL workspace.");
builder.Services.AddSingleton(new RunCatalog(root.FullName,Path.Combine(root.FullName,"observation/catalog.json")));
builder.Services.AddSingleton<ObservationService>();
builder.Services.AddHostedService<ObservationRefresh>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents(o=>
{
    o.MaxBufferedUnacknowledgedRenderBatches=2;
    o.DisconnectedCircuitRetentionPeriod=TimeSpan.FromMinutes(2);
    o.DisconnectedCircuitMaxRetained=8;
});
builder.Services.ConfigureHttpJsonOptions(o=>
{
    foreach(var converter in EconomicJson.Options.Converters)o.SerializerOptions.Converters.Add(converter);
});
var app = builder.Build();
app.Use(async(context,next)=>
{
    // Local investigation only: a hostile Host header or accidental network bind
    // must not turn the catalog into a public source-data service.
    if(context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote) ||
       context.Request.Host.Host is not ("localhost" or "127.0.0.1" or "::1"))
    {context.Response.StatusCode=403;return;}
    context.Response.Headers.XContentTypeOptions="nosniff";
    context.Response.Headers["Referrer-Policy"]="no-referrer";
    context.Response.Headers["X-Frame-Options"]="DENY";
    try {await next();}
    catch(Exception ex)
    {
        if(context.Response.HasStarted)throw;
        context.Response.StatusCode=ex is KeyNotFoundException?404:409;
        await context.Response.WriteAsJsonAsync(new { error="Source unavailable, inconsistent, or outside the approved catalog. No result was substituted." });
    }
});
app.UseAntiforgery();
app.MapGet("/api/runs",(RunCatalog catalog)=>catalog.List());
app.MapGet("/api/runs/{key}/scene",(string key,long? at,long? generation,long? after,bool? live,ObservationService service)=>
    service.ReadAsync(key,p=>p.Scene(at??p.ProducerTimeMs,generation??0,after,live??false)));
app.MapGet("/api/runs/{key}/network",(string key,ObservationService service)=>service.ReadAsync(key,p=>p.Network));
app.MapGet("/api/runs/{key}/vehicle/{id}",(string key,string id,long? at,long? generation,bool? hindsight,ObservationService service)=>
    service.ReadAsync(key,p=>p.Vehicle(id,at??p.ProducerTimeMs,generation??0,hindsight??false)));
app.MapGet("/api/runs/{key}/costs/{id}",(string key,string id,long? at,long? generation,bool? hindsight,ObservationService service)=>
    service.ReadAsync(key,p=>p.Costs(id,at??p.ProducerTimeMs,generation??0,hindsight??false)));
app.MapGet("/api/runs/{key}/learning",(string key,long? at,bool? hindsight,ObservationService service)=>service.ReadAsync(key,p=>p.Learning(at??p.ProducerTimeMs,hindsight??false)));
app.MapGet("/api/compare",(string left,string right,ObservationService service)=>service.CompareAsync(left,right));
app.MapGet("/api/diagnostics",(ObservationService service)=>service.StatisticsAsync());
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public sealed class ObservationRefresh(ObservationService service,ILogger<ObservationRefresh> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while(await timer.WaitForNextTickAsync(stoppingToken))
            try {await service.RefreshActiveAsync();} catch(Exception e){logger.LogWarning("Observer refresh unavailable ({Type}).",e.GetType().Name);}
    }
}
public partial class Program;
