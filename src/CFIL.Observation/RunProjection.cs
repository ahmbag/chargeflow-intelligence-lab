using System.Text.Json;
using System.Diagnostics;
using CFIL.Domain;
using CFIL.Infrastructure;
using CFIL.Simulation;

namespace CFIL.Observation;

internal sealed record RecordedEvent(long TimeMs,long Sequence,string Type,string Vehicle,JsonElement Payload);
internal sealed record RecordedTrace(long TimeMs,long Sequence,string Type,JsonElement Payload);
internal sealed record PowerPoint(long TimeMs,long Sequence,double Kw);
internal sealed record MeterPoint(long TimeMs,decimal Energy,string SessionId);

/// <summary>One bounded read-only projection per run, shared by every viewing circuit.</summary>
public sealed partial class RunProjection
{
    public RunSource Source { get; }
    public Scenario Scenario { get; }
    public VehicleDefinition[] Population { get; }
    public NetworkView Network { get; }
    public RunOverview Overview => RunCatalog.Overview(Source);
    public SemaphoreSlim Gate { get; }=new(1,1);
    private readonly SourceReader eventsReader,telemetryReader,snapshotsReader,traceReader,costReader;
    private readonly List<RecordedEvent> events=[];
    private readonly List<RecordedTrace> traces=[];
    private readonly List<LinePointer> telemetry=[],snapshots=[];
    private readonly Dictionary<long,SimulationSnapshot> snapshotFrames=[];
    private readonly Dictionary<long,TelemetryFrame> telemetryFrames=[];
    private sealed record TelemetryFrame(string SchemaVersion,long SimulationTimeMs,VehicleSnapshot[] Vehicles);
    private long frameLoads,lastRefreshTimestamp,telemetryEventFence=-1;
    private readonly SortedList<long,long> sequences=[];
    private readonly Dictionary<string,List<PowerPoint>> powers=new(StringComparer.Ordinal);
    private readonly Dictionary<string,List<MeterPoint>> meters=new(StringComparer.Ordinal);
    private readonly Dictionary<string,JsonElement> economics=new(StringComparer.Ordinal);
    private readonly Dictionary<string,long> planTimes=new(StringComparer.Ordinal);
    private long eventSequence,traceSequence,costSequence,lastEventTime,lastTraceTime,lastCostTime,validatedTime;
    private JsonElement? financialReport;
    private readonly JsonElement? economicInputs;
    private readonly bool controlledPlanning;
    public string? Failure { get; private set; }
    public bool Ready { get; private set; }
    public DateTimeOffset LastUsed { get; set; }=DateTimeOffset.UtcNow;
    public long ProducerTimeMs => Math.Max(lastEventTime,validatedTime);
    public RunProjection(RunSource source)
    {
        Source=source;
        controlledPlanning=J.Read(Path.Combine(source.Directory,"manifest.json")).Get("chargingPlanning")?.Text("mode")=="CONTROLLED";
        if(!File.Exists(Path.Combine(source.Directory,"snapshots.jsonl"))) throw new InvalidDataException("Replay data missing.");
        Scenario=JsonDefaults.Deserialize<Scenario>(File.ReadAllText(Path.Combine(source.Directory,"scenario.json")));
        var inputs=Path.Combine(source.Directory,"external-inputs.json");
        if(!File.Exists(inputs))throw new InvalidDataException("Recorded population is missing; the observer cannot generate replacement inputs.");
        Population=JsonDefaults.Deserialize<RunExecutor.ExternalInputs>(File.ReadAllText(inputs)).Population;
        Network=NetworkReader.Read(Path.Combine(source.Directory,"sumo/network.net.xml"));
        eventsReader=new(Path.Combine(source.Directory,"events.jsonl")); telemetryReader=new(Path.Combine(source.Directory,"telemetry.jsonl"));
        snapshotsReader=new(Path.Combine(source.Directory,"snapshots.jsonl"));traceReader=new(Path.Combine(source.Directory,"decisions.jsonl"));costReader=new(Path.Combine(source.Directory,"cost-observations.jsonl"));
        if(File.Exists(Path.Combine(source.Directory,"economic-inputs.json"))) economicInputs=J.Read(Path.Combine(source.Directory,"economic-inputs.json"));
    }
    public async Task InitializeAsync()
    {
        await Gate.WaitAsync();
        try
        {
            while(RefreshCore()>0) { }
            Ready=true;
        }
        catch { Failure="Source integrity or observation limit violation. Reload a verified source.";throw; }
        finally { Gate.Release(); }
    }
    public async Task RefreshAsync()
    {
        // Join the same FIFO wait queue as browser reads. A nonblocking try-lock
        // can repeatedly lose to periodic viewers and starve live ingestion even
        // though each individual query is fast. The single refresh worker awaits
        // completion, so queuing here does not create an unbounded refresh backlog.
        await Gate.WaitAsync();
        try { if(Failure is null) RefreshCore(); }
        catch { Failure="Source integrity or observation limit violation. Display frozen at last valid snapshot."; }
        finally { Gate.Release(); }
    }
    internal void RefreshBeforeRead()
    {
        // Readers share the same append cursor and freshness clock. If the timer
        // was delayed by scheduling/GC, the next reader catches up once, under the
        // existing gate, rather than publishing a stale frame with a new receipt.
        if(!Ready || Failure is not null || Stopwatch.GetElapsedTime(lastRefreshTimestamp)<TimeSpan.FromMilliseconds(500))return;
        try{RefreshCore();}
        catch{Failure="Source integrity or observation limit violation. Display frozen at last valid snapshot.";throw;}
    }
    private T Frame<T>(Dictionary<long,T> cache,LinePointer pointer,SourceReader reader)
    {
        if(cache.TryGetValue(pointer.Offset,out var value))return value;
        // Completed JSONL frames are immutable. Retain only two parsed frames per
        // stream; replay still selects by time before consulting this cache. This
        // avoids reparsing a large population snapshot on every animation tick.
        value=reader.Read<T>(pointer,JsonDefaults.Options);frameLoads++;
        if(cache.Count==2)cache.Remove(cache.Keys.First());
        cache.Add(pointer.Offset,value);return value;
    }
    private int RefreshCore()
    {
        void AcceptEvent(JsonElement e,LinePointer _)
        {
            var seq=e.Long("sequence");var time=e.Long("simulationTimeMs");
            if(e.Text("schemaVersion")!="1.0")throw new InvalidDataException("Unsupported source schema.");
            if(seq!=eventSequence+1 || time<lastEventTime || e.Text("runId")!=OverviewId) throw new InvalidDataException("Event sequence/run gap.");
            eventSequence=seq;lastEventTime=time;sequences[time]=seq;
            var type=e.Text("eventType");var vehicle=e.Text("entityId");var p=e.GetProperty("payload");
            if(type=="ChargingPowerChanged")
            {
                if(!powers.TryGetValue(vehicle,out var values)) powers[vehicle]=values=[];
                var point=new PowerPoint(time,seq,p.Num("assignedPowerKw"));
                // Only visual power telemetry is coalesced. Its true timestamp is retained,
                // so a seek inside a bucket can never expose a later measurement.
                if(values.Count>0 && values[^1].TimeMs/60000==time/60000) values[^1]=point; else values.Add(point);
            }
            else events.Add(new(time,seq,type,vehicle,p.Clone()));
        }
        // Split the existing 8 MiB event budget between the first read and the
        // verification read after observing newly published telemetry below.
        var count=eventsReader.ReadBatch(AcceptEvent,4*1024*1024);
        count+=snapshotsReader.ReadBatch((e,p)=>
        {
            if(e.Text("schemaVersion")!="1.0" || e.Text("runId")!=OverviewId)throw new InvalidDataException("Snapshot schema or run identity mismatch.");
            if(snapshots.Count>0 && (p.TimeMs<snapshots[^1].TimeMs || e.Long("lastEventSequence")<snapshots[^1].Sequence))throw new InvalidDataException("Snapshot order regressed.");
            snapshots.Add(p with { Sequence=e.Long("lastEventSequence") });
        });
        var newTelemetry=telemetryReader.ReadBatch((e,p)=>
        {
            if(e.Text("schemaVersion")!="1.0")throw new InvalidDataException("Unsupported telemetry schema.");
            if(telemetry.Count>0 && p.TimeMs<=telemetry[^1].TimeMs)throw new InvalidDataException("Telemetry order regressed.");
            telemetry.Add(p);
        });
        count+=newTelemetry;
        if(newTelemetry>0)telemetryEventFence=eventsReader.PublishedLength;
        count+=eventsReader.ReadBatch(AcceptEvent,4*1024*1024);
        count+=traceReader.ReadBatch((e,_)=>
        {
            var seq=e.Long("sequence");if(seq!=traceSequence+1) throw new InvalidDataException("Decision trace gap.");traceSequence=seq;
            var p=e.GetProperty("payload");var type=e.Text("type");
            var context=p.Get("context");var plan=p.Text("planId",context?.Text("planId")??"");
            if(context is {} identity && identity.Text("runId")!=OverviewId)throw new InvalidDataException("Decision trace belongs to another run.");
            var time=context?.Long("timeMs")??p.Get("outcome")?.Long("outcomeTimeMs")??p.Get("route")?.Long("queriedAtMs")??lastTraceTime;
            if(context is not null) planTimes[context.Value.Text("planId")]=time;
            if(type is "APPLIED" or "APPLYING" && planTimes.TryGetValue(plan,out var cycle)) time=cycle;
            if(type=="Completed") time=Scenario.DurationSeconds*1000L;
            lastTraceTime=Math.Max(lastTraceTime,time);traces.Add(new(time,seq,type,p.Clone()));
        });
        count+=costReader.ReadBatch((e,_)=>
        {
            if(e.Text("version")!="ECONOMIC-SOURCE-V1" || e.Text("runId")!=OverviewId)throw new InvalidDataException("Financial source schema or run identity mismatch.");
            var seq=e.Long("sequence");var time=e.Long("timeMs");
            if(seq!=costSequence+1 || time<lastCostTime) throw new InvalidDataException("Financial source gap.");
            costSequence=seq;lastCostTime=time;var p=e.GetProperty("payload");
            if(e.Text("type")=="Decision") economics[p.Text("planId")]=p.Clone();
            if(e.Text("type")=="Session" && p.Get("startedMs") is { ValueKind:JsonValueKind.Number })
            {
                var id=p.Text("vehicleId");if(!meters.TryGetValue(id,out var list)) meters[id]=list=[];
                var point=new MeterPoint(time,decimal.Parse(p.Text("gridEnergyKwh"),System.Globalization.CultureInfo.InvariantCulture),p.Text("sessionId"));
                // A new session resets the cumulative session meter; never interpret its
                // reset as negative physical charging power.
                list.Add(point);
            }
        });
        // Multi-day indexes retain lifecycle references across the entire horizon;
        // frames still use the same two-entry caches and bounded source reads.
        var days=Scenario.MultiDay?.Days??1;
        if(events.Count>100000*days || traces.Count>50000*days || telemetry.Count+snapshots.Count+meters.Values.Sum(v=>v.Count)+powers.Values.Sum(v=>v.Count)>500000*days)
            throw new InvalidDataException("Observation index budget exceeded.");
        // JsonlEventStore flushes events BEFORE telemetry. The sidecar clock is
        // only the last business-event time, so it can remain unchanged for many
        // simulated minutes. A complete telemetry frame supplies the later clock
        // only after the contiguous event reader covers a byte boundary captured
        // AFTER that frame was observed. A partial event/gap cannot be bypassed.
        if(telemetryEventFence>=0 && eventsReader.Offset>=telemetryEventFence && telemetry.Count>0)
            validatedTime=Math.Max(validatedTime,telemetry[^1].TimeMs);
        var checkpoint=snapshots.LastOrDefault(p=>p.Sequence<=eventSequence);
        if(checkpoint is not null)validatedTime=Math.Max(validatedTime,checkpoint.TimeMs);
        var progressPath=Path.Combine(Source.Directory,"producer-progress.json");
        if(File.Exists(progressPath))
        {
            var progress=J.Read(progressPath);
            if(progress.Long("sequence")<=eventSequence && progress.Long("byteOffset")<=eventsReader.Offset)
                validatedTime=Math.Max(validatedTime,progress.Long("simulationTimeMs"));
        }
        var reportPath=Path.Combine(Source.Directory,"economic-report.json");
        // The financial exporter writes its final report before atomically
        // publishing the terminal manifest. File existence alone is not a
        // completion boundary: reading mid-export could freeze a healthy view.
        var manifestStatus=J.Read(Path.Combine(Source.Directory,"manifest.json")).Text("status");
        if(financialReport is null && manifestStatus is "completed" or "failed" or "cancelled" && File.Exists(reportPath))
        {
            if(Source.ExpectedFinancialHash is not null && RunCatalog.HashFile(reportPath)!=Source.ExpectedFinancialHash)
                throw new InvalidDataException("Financial report checksum mismatch.");
            financialReport=J.Read(reportPath);
            if(financialReport.Value.Text("runId")!=OverviewId)throw new InvalidDataException("Financial report belongs to another run.");
        }
        lastRefreshTimestamp=Stopwatch.GetTimestamp();return count;
    }
    private string OverviewId => SourceRunId ??= J.Read(Path.Combine(Source.Directory,"manifest.json")).Text("runId");
    private string? SourceRunId;
    public ProjectionStatistics Statistics => new(1,new[]{eventsReader,telemetryReader,snapshotsReader,traceReader,costReader}.Sum(r=>r.BytesRead),
        new[]{eventsReader,telemetryReader,snapshotsReader,traceReader,costReader}.Sum(r=>r.Passes),events.Count,eventSequence,telemetry.Count+snapshots.Count,
        FrameLoads:frameLoads,SnapshotFrames:snapshotFrames.Count,TelemetryFrames:telemetryFrames.Count);
    private static int AtOrBefore<T>(IList<T> values,long time,Func<T,long> clock)
    {
        var lo=0;var hi=values.Count-1;var best=-1;
        while(lo<=hi) {var mid=(lo+hi)/2;if(clock(values[mid])<=time){best=mid;lo=mid+1;}else hi=mid-1;}
        return best;
    }
    public SceneSnapshot Scene(long requested,long generation,long? after=null,bool live=false)
    {
        if(Failure is not null) throw new InvalidDataException(Failure);
        var time=Math.Clamp(requested,0,ProducerTimeMs);
        var index=AtOrBefore(snapshots,time,p=>p.TimeMs);
        // Files are flushed independently. A frame cannot advance the observer past
        // the contiguous event prefix already validated by this shared reader.
        while(index>=0 && snapshots[index].Sequence>eventSequence)index--;
        if(index<0)
        {
            // Initial snapshots and their event prefix flush independently. An
            // unfinished first frame is pending data, not a corrupt live source.
            if(Overview.Status is "running" or "initializing")throw new ObservationPendingException();
            throw new InvalidDataException("No complete source snapshot yet.");
        }
        var snapshot=Frame(snapshotFrames,snapshots[index],snapshotsReader);
        var state=snapshot.Vehicles.ToDictionary(v=>v.Id,StringComparer.Ordinal);
        if(state.Count!=Population.Length || Population.Any(v=>!state.ContainsKey(v.Id)))throw new InvalidDataException("Snapshot population mismatch.");
        var positionTimes=state.Keys.ToDictionary(id=>id,_=>snapshot.SimulationTimeMs);
        var energyTimes=new Dictionary<string,long>(positionTimes);
        var ti=AtOrBefore(telemetry,time,p=>p.TimeMs);var displayTime=snapshot.SimulationTimeMs;
        if(ti>=0 && telemetry[ti].TimeMs>=displayTime)
        {
            var frame=Frame(telemetryFrames,telemetry[ti],telemetryReader);displayTime=telemetry[ti].TimeMs;
            foreach(var v in frame.Vehicles)
            {
                if(!state.ContainsKey(v.Id))throw new InvalidDataException("Unknown vehicle in telemetry.");
                state[v.Id]=v;positionTimes[v.Id]=displayTime;energyTimes[v.Id]=displayTime;
            }
        }
        var parkState=snapshot.Parks.ToDictionary(p=>p.Id,p=>new MutablePark(p));
        if(parkState.Count!=Scenario.Parks.Length || Scenario.Parks.Any(p=>!parkState.ContainsKey(p.Id)))throw new InvalidDataException("Snapshot park population mismatch.");
        foreach(var e in events.Where(e=>e.TimeMs>=snapshot.SimulationTimeMs && e.Sequence>snapshot.LastEventSequence && e.TimeMs<=time))
        {
            ApplyPark(e,parkState);
            if(!state.TryGetValue(e.Vehicle,out var vehicle)) continue;
            if(e.TimeMs<positionTimes[e.Vehicle]) continue;
            var status=e.Type switch {"VehicleEnteredHighway"=>"Driving","VehicleStateObserved"=>e.Payload.Text("state",vehicle.Status),
                "QueueEntered"=>"Queued","ChargingStarted"=>"Charging","VehicleDepartedPark"=>"Driving",
                "VehicleReachedDestination"=>"Arrived","VehicleStranded"=>"Stranded",_=>vehicle.Status};
            var park=e.Payload.Text("parkId",vehicle.ParkId??"");var bay=e.Payload.Text("bayId",vehicle.BayId??"");
            // Energy completion is not physical departure, and a stranded truck
            // can still obstruct its bay. Only an observed departure clears it.
            if(status is "Driving" or "Arrived") {park="";bay="";}
            if(e.Payload.Get("soc") is { } soc) {vehicle=vehicle with {EnergyKwh=soc.GetDouble()*vehicle.CapacityKwh};energyTimes[e.Vehicle]=e.TimeMs;}
            else if(e.Payload.Get("energyKwh") is { ValueKind:JsonValueKind.Number } energy){vehicle=vehicle with {EnergyKwh=energy.GetDouble()};energyTimes[e.Vehicle]=e.TimeMs;}
            state[e.Vehicle]=vehicle with {Status=status,ParkId=park.Length==0?null:park,BayId=bay.Length==0?null:bay,DistanceM=e.Payload.Num("distanceM",vehicle.DistanceM)};
        }
        foreach(var e in events.Where(e=>e.Type=="ChargingParkCapacityChanged" && e.TimeMs<=time)) ApplyPark(e,parkState);
        var terminal=Overview.Status is "completed" or "failed" or "cancelled" or "incomplete";
        var views=Population.Select(v=>
        {
            var s=state[v.Id];var status=s.Status;
            if(terminal && time>=ProducerTimeMs && status is not ("Arrived" or "Stranded" or "Scheduled")) status="Incomplete";
            var point=NetworkReader.Locate(Network,s.EdgeId,s.PositionKm*1000);
            return new VehicleView(v.Id,v.Kind.ToString(),status,s.CapacityKwh==0?0:s.EnergyKwh/s.CapacityKwh,s.EnergyKwh,s.DistanceM,
                point.X,point.Y,point.Heading,s.EdgeId,s.ParkId,s.BayId,positionTimes[v.Id],energyTimes[v.Id],time-positionTimes[v.Id]>Scenario.TelemetryIntervalSeconds*1000L,
                s.EdgeId.Length==0?"No observed network position":!Network.Edges.Any(e=>e.Id==s.EdgeId)?"Unknown edge; map position unavailable":"Edge projection; exact lane unavailable",v.LengthM);
        }).ToArray();
        var parks=Scenario.Parks.Select(p=>Park(p,parkState[p.Id],views,time)).ToArray();
        var si=AtOrBefore(sequences.Keys,time,x=>x);var sequence=si<0?0:sequences.Values[si];
        DateTimeOffset? published=null;var progress=Path.Combine(Source.Directory,"producer-progress.json");var producerTime=ProducerTimeMs;
        if(File.Exists(progress))
        {
            var producer=J.Read(progress);producerTime=Math.Max(producerTime,producer.Long("simulationTimeMs"));
            if(DateTimeOffset.TryParse(producer.Text("flushedAtUtc"),out var date))published=date;
        }
        var health=live && (producerTime-displayTime>600000 || published is null || DateTimeOffset.UtcNow-published>TimeSpan.FromSeconds(8))?"LIVE_DELAYED":"CURRENT";
        return new(Source.Key,OverviewId,generation,time,producerTime,displayTime,sequence,"CFIL06-READ-V1",DateTimeOffset.UtcNow,published,
            live && !terminal?"LIVE":"REPLAY",after is not null && after!=sequence?"RESYNCHRONIZED_SNAPSHOT":"SNAPSHOT",health,views,parks,
            views.GroupBy(v=>v.Status).ToDictionary(g=>g.Key,g=>g.Count()),["Exact lane and instantaneous actual power are not archived.","Positions are held between observations; no extrapolation or traffic simulation."],
            new("events.jsonl + snapshots.jsonl + telemetry.jsonl + producer-progress.json",sequence,time));
    }
    private sealed class MutablePark(ParkSnapshot snapshot)
    {
        public double Available=snapshot.AvailableGridKw;
        public Dictionary<string,string> Occupants=new(snapshot.Occupants);
        public List<string> Queue=snapshot.Queue.ToList();public HashSet<string> Inbound=snapshot.Inbound.ToHashSet();public HashSet<string> Blocked=[];
    }
    private void ApplyPark(RecordedEvent e,Dictionary<string,MutablePark> parks)
    {
        if(e.Type=="VehicleStranded" || e.Type=="VehicleStateObserved" && e.Payload.Text("state")=="Stopping")
        {
            // Match the producer's cancellation semantics without freeing a
            // physically occupied bay or turning the obstruction into capacity.
            foreach(var park in parks.Values){park.Queue.Remove(e.Vehicle);park.Inbound.Remove(e.Vehicle);}
            return;
        }
        var id=e.Type=="ChargingParkCapacityChanged"?e.Vehicle:e.Payload.Text("parkId");
        if(!parks.TryGetValue(id,out var p)) return;
        if(e.Type=="ChargingParkCapacityChanged") {p.Available=e.Payload.Num("availableGridKw");p.Blocked=e.Payload.GetProperty("blockedBays").Items().Select(x=>x.GetString()!).ToHashSet();}
        if(e.Type=="VehicleStateObserved" && e.Payload.Text("state")=="ApproachingPark" &&
            !(controlledPlanning && Population.Any(v=>v.Id==e.Vehicle && v.Kind==VehicleKind.Truck)))p.Inbound.Add(e.Vehicle);
        if(e.Type=="ChargingParkApproached") p.Inbound.Remove(e.Vehicle);
        if(e.Type=="QueueEntered") {p.Inbound.Remove(e.Vehicle);if(!p.Queue.Contains(e.Vehicle))p.Queue.Add(e.Vehicle);}
        if(e.Type=="ChargingStarted") {p.Queue.Remove(e.Vehicle);p.Occupants[e.Payload.Text("bayId")]=e.Vehicle;}
        if(e.Type=="VehicleStateObserved" && e.Payload.Text("state")=="Connecting") {p.Queue.Remove(e.Vehicle);p.Occupants[e.Payload.Text("bayId")]=e.Vehicle;}
        if(e.Type is "VehicleDepartedPark" or "QueueAbandoned") {p.Queue.Remove(e.Vehicle);p.Inbound.Remove(e.Vehicle);foreach(var bay in p.Occupants.Where(x=>x.Value==e.Vehicle).Select(x=>x.Key).ToArray())p.Occupants.Remove(bay);}
    }
    private ChargingParkStatus Park(ChargingPark p,MutablePark state,VehicleView[] vehicles,long time)
    {
        var assigned=0d;decimal sumPower=0;var observed=0;
        foreach(var v in vehicles.Where(v=>v.ParkId==p.Id && v.Status=="Charging"))
        {
            if(powers.TryGetValue(v.Id,out var power)){var index=AtOrBefore(power,time,x=>x.TimeMs);if(index>=0)assigned+=power[index].Kw;}
            if(meters.TryGetValue(v.Id,out var samples)){var index=AtOrBefore(samples,time,x=>x.TimeMs);if(index>0 && samples[index].SessionId==samples[index-1].SessionId && samples[index].TimeMs>samples[index-1].TimeMs)
                {sumPower+=(samples[index].Energy-samples[index-1].Energy)*3600000/(samples[index].TimeMs-samples[index-1].TimeMs);observed++;}}
        }
        var edge=Network.Edges.FirstOrDefault(e=>e.Id==p.Id+"-queue");var point=edge?.Shape.FirstOrDefault()??new Point(p.PositionKm*1000,0);
        var intents=traces.Where(t=>t.Type=="Intent" && t.TimeMs<=time && t.Payload.Text("parkId")==p.Id && t.Payload.Long("expiresMs")>=time)
            .Select(t=>t.Payload.Text("vehicleId")).Distinct().Where(id=>vehicles.Any(v=>v.Id==id && v.Status is "Driving" or "ApproachingPark")).ToArray();
        return new(p.Id,point.X,point.Y,p.AccessWidthM,p.QueueCapacity,p.GridPowerKw,state.Available,assigned,observed==0?null:(double)sumPower,
            "Assigned power is sampled from physical events. Actual power is a metered interval mean, not an instantaneous observation.",state.Queue.ToArray(),state.Inbound.ToArray(),intents,
            p.Bays.Select(b=>new BayView(b.Id,state.Occupants.GetValueOrDefault(b.Id),b.LengthM,b.WidthM,b.MaxTrailerLengthM,b.TurningRadiusM,b.DriveThrough,
                b.Connector.Type,b.Connector.MaxGridPowerKw,b.Conflicts,b.ExitBlockedBy,state.Blocked.Contains(b.Id))).ToArray(),new("snapshots.jsonl + events.jsonl",sequences.Where(s=>s.Key<=time).LastOrDefault().Value,time));
    }
}
