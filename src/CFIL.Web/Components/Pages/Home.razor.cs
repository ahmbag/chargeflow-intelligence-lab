using System.Text.Json;
using CFIL.Observation;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CFIL.Web.Components.Pages;

public partial class Home : IAsyncDisposable
{
    [Inject] private ObservationService Observer {get;set;}=default!;
    [Inject] private IJSRuntime Js {get;set;}=default!;
    [Inject] private ILogger<Home> Logger {get;set;}=default!;
    private readonly string[] sections=["Runs","Live / Replay","Vehicle & Decisions","Economics","Compare & Learning"];
    private RunOverview[] runs=[];
    private string section="Runs",runKey="cfil05-e2-715",vehicleId="truck-001",parkId="",rightKey="cfil05-e1-715",error="",filter="",kind="Truck";
    private SceneSnapshot? scene,rightScene;
    private NetworkView? network;
    private VehicleTimeline? vehicle,rightVehicle;
    private CostReconciliationView? costs,rightCosts;
    private LearningView? learning;
    private PairComparison? comparison;
    private JsonElement[] tariffs=[];
    private string[] route=[];
    private long generation=0,cursor=0;
    private bool busy,playing,live,hindsight,disposed,drawPending,interactive,waitingForSource;
    private int speed=60;
    private IJSObjectReference? module;
    private DotNetObjectReference<Home>? reference;
    private CancellationTokenSource cancellation=new();
    private Task? clockLoop;
    private RunOverview? Current=>runs.FirstOrDefault(r=>r.Key==runKey);
    private IEnumerable<VehicleView> VisibleVehicles=>scene?.Vehicles.Where(v=>(kind=="All"||v.Kind==kind)&&(filter.Length==0||v.Id.Contains(filter,StringComparison.OrdinalIgnoreCase)))??[];
    protected override void OnInitialized(){runs=Observer.Catalog.List();}
    protected override async Task OnAfterRenderAsync(bool first)
    {
        if(first)
        {
            module=await Js.InvokeAsync<IJSObjectReference>("import","./observer.js");
            reference=DotNetObjectReference.Create(this);await module.InvokeVoidAsync("attach",reference);
            interactive=true;StateHasChanged();
            clockLoop=TickAsync();
        }
        if(drawPending && module is not null && scene is not null && network is not null)
        {
            drawPending=false;
            await module.InvokeVoidAsync("draw",network,scene,vehicleId,parkId,route);
        }
    }
    private async Task TickAsync()
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while(await timer.WaitForNextTickAsync(cancellation.Token))
                if((live||playing||waitingForSource)&&!busy)await InvokeAsync(async()=>
                {
                    if(playing&&!live)cursor=Math.Min(cursor+speed*500L,scene?.ProducerTimeMs??cursor);
                    await LoadAsync();StateHasChanged();
                });
        }
        catch(OperationCanceledException){}
    }
    private async Task OpenRun(string key)
    {
        generation++;runKey=key;cursor=0;live=false;playing=false;hindsight=false;waitingForSource=false;
        scene=null;vehicle=null;costs=null;learning=null;comparison=null;rightScene=null;rightVehicle=null;parkId="";
        section="Live / Replay";await LoadAsync();
    }
    private async Task LoadAsync()
    {
        if(busy)return;
        busy=true;error="";
        var token=generation;var key=runKey;var right=rightKey;var requested=cursor;var selected=vehicleId;var retrospective=hindsight;
        try
        {
            var data=await Observer.ReadAsync(key,p=>
            {
                var s=p.Scene(live?p.ProducerTimeMs:requested,token,scene?.SourceSequence,live);
                var id=p.Population.Any(v=>v.Id==selected)?selected:p.Population.First(v=>v.Kind==CFIL.Domain.VehicleKind.Truck).Id;
                // Map ticks need the selected route, not a second full scene and
                // every historical decision. Load that detail only in its views.
                return (Scene:s,Overview:p.Overview,Network:p.Network,SelectedId:id,Route:p.Route(id,s.CursorMs),
                    Vehicle:section is "Vehicle & Decisions" or "Economics" or "Compare & Learning"?p.Vehicle(id,s.CursorMs,token,retrospective):null,
                    Costs:section is "Economics" or "Compare & Learning"?p.Costs(id,s.CursorMs,token,retrospective):null,
                    Learning:section=="Compare & Learning"?p.Learning(s.CursorMs,retrospective):null,
                    Tariffs:p.PublishedTariffs(parkId,s.CursorMs));
            });
            // A circuit owns its generation. Switching run or seeking invalidates
            // every outstanding response, even if its I/O finishes successfully.
            if(disposed||token!=generation||key!=runKey)return;
            waitingForSource=false;
            runs=runs.Select(r=>r.Key==key?data.Overview:r).ToArray();
            scene=data.Scene;cursor=scene.CursorMs;network=data.Network;vehicle=data.Vehicle;vehicleId=data.SelectedId;route=data.Route;
            if(scene.Mode=="REPLAY")live=false;
            costs=data.Costs;learning=data.Learning;tariffs=data.Tariffs;drawPending=true;
            if(section=="Compare & Learning" && comparison?.Comparable==true)
            {
                var pair=await Observer.ReadAsync(right,p=>(Scene:p.Scene(data.Scene.CursorMs,token),Vehicle:p.Vehicle(data.SelectedId,data.Scene.CursorMs,token,retrospective),Costs:p.Costs(data.SelectedId,data.Scene.CursorMs,token,retrospective)));
                var refreshedComparison=await Observer.CompareAsync(key,right,data.Scene.CursorMs,retrospective);
                if(token==generation && key==runKey && right==rightKey){rightScene=pair.Scene;rightVehicle=pair.Vehicle;rightCosts=pair.Costs;comparison=refreshedComparison;}
            }
        }
        catch(ObservationPendingException)
        {
            if(token==generation)
            {
                waitingForSource=true;
                error="Source is starting. Waiting for a complete, validated initial snapshot…";
            }
        }
        catch(Exception exception)
        {
            Logger.LogWarning(exception,"Observer could not read approved run {RunKey}",key);
            if(token==generation)
            {
                if(scene is not null)cursor=scene.CursorMs;
                error="Source unavailable or inconsistent. The last valid snapshot is retained when available. Choose a verified catalog run and retry.";
                playing=false;live=false;waitingForSource=false;
            }
        }
        finally
        {
            busy=false;
            // A superseded request was discarded; service the newest user intent.
            if(!disposed&&token!=generation)await LoadAsync();
        }
    }
    private async Task Tab(string name){generation++;section=name;drawPending=true;if(name=="Runs")waitingForSource=false;if(scene is not null)await LoadAsync();}
    private async Task Seek(ChangeEventArgs e){if(long.TryParse(e.Value?.ToString(),out var seconds))await SeekTo(seconds*1000);}
    private async Task SeekTo(long time){generation++;cursor=time;live=false;playing=false;await LoadAsync();}
    private async Task Next()
    {
        var token=generation;var key=runKey;var at=cursor;var id=vehicleId;
        var next=await Observer.ReadAsync(key,p=>p.NextEvent(at,id));
        if(token==generation && key==runKey && next is {} time)await SeekTo(time);
    }
    private async Task GoLive(){generation++;live=true;playing=false;await LoadAsync();}
    private void Pause(){live=false;playing=false;waitingForSource=false;}
    private async Task HindsightChanged(ChangeEventArgs e){hindsight=e.Value is true;generation++;await LoadAsync();}
    [JSInvokable] public async Task SelectVehicle(string id){vehicleId=id;parkId="";generation++;section="Vehicle & Decisions";await LoadAsync();StateHasChanged();}
    [JSInvokable] public async Task SelectPark(string id){parkId=id;generation++;section="Live / Replay";await LoadAsync();StateHasChanged();}
    [JSInvokable] public async Task Resynchronize(){generation++;await LoadAsync();StateHasChanged();}
    private async Task Compare()
    {
        comparison=null;rightScene=null;rightVehicle=null;error="";
        var token=++generation;var key=runKey;var right=rightKey;var at=cursor;var retrospective=hindsight;
        try
        {
            var result=await Observer.CompareAsync(key,right,at,retrospective);
            if(token!=generation || key!=runKey || right!=rightKey)return;
            comparison=result;await LoadAsync();
        }
        catch{if(token==generation)error="Comparison unavailable. Select two distinct catalog runs with recorded evidence.";}
    }
    private void RightChanged(ChangeEventArgs e)
    {generation++;rightKey=e.Value!.ToString()!;comparison=null;rightScene=null;rightVehicle=null;rightCosts=null;}
    private async Task Example(int seed,string baseline)
    {
        var opening=OpenRun($"cfil05-e2-{seed}");var token=generation;await opening;
        if(token!=generation)return;
        rightKey=$"cfil05-{baseline}-{seed}";section="Compare & Learning";cursor=86400000;await Compare();
    }
    private string Time(long ms)=>Current?.DurationMs>86400000
        ?$"Day {ms/86400000+1} · {TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss") }"
        :ms>=86400000?"24:00:00":TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss");
    private static string Number(double? value,string suffix="")=>value is null?"N/A":value.Value.ToString("0.##",System.Globalization.CultureInfo.InvariantCulture)+suffix;
    private static string Money(CFIL.Domain.Money value)=>value.Amount.ToString("0.00##########################",System.Globalization.CultureInfo.InvariantCulture)+" "+value.Currency;
    public async ValueTask DisposeAsync()
    {
        disposed=true;cancellation.Cancel();if(clockLoop is not null)await clockLoop;cancellation.Dispose();
        if(module is not null){try{await module.InvokeVoidAsync("detach");await module.DisposeAsync();}catch(JSDisconnectedException){}}
        reference?.Dispose();
    }
}
