using System.Globalization;
using System.Text.Json;
using CFIL.Domain;
using Microsoft.Data.Sqlite;

namespace CFIL.Observation;

public sealed partial class RunProjection
{
    private static TimelineEntry Entry(RecordedEvent e)=>new(e.TimeMs,e.Sequence,e.Type,PublicFacts(e.Payload),new("events.jsonl",e.Sequence,e.TimeMs));
    private static TimelineEntry Entry(RecordedTrace e)=>new(e.TimeMs,e.Sequence,e.Type,PublicFacts(e.Payload),new("decisions.jsonl",e.Sequence,e.TimeMs));
    public static JsonElement PublicFacts(JsonElement element)
    {
        // Never pass serialized producer exceptions, process commands, or storage
        // configuration through to a browser, even in an expandable raw-facts panel.
        object? Map(JsonElement e)=>e.ValueKind switch
        {
            JsonValueKind.Object=>e.EnumerateObject().Where(p=>!new[]{"source","command","binding","initialModel","key"}.Contains(p.Name)
                && !p.Name.EndsWith("Path",StringComparison.OrdinalIgnoreCase) && !p.Name.EndsWith("Directory",StringComparison.OrdinalIgnoreCase))
                .ToDictionary(p=>p.Name,p=>p.Name=="error" && p.Value.ValueKind!=JsonValueKind.Null?"Recorded operation failed; technical details remain on the server.":Map(p.Value)),
            JsonValueKind.Array=>e.EnumerateArray().Select(Map).ToArray(),JsonValueKind.String=>e.GetString(),
            JsonValueKind.Number=>e.Clone(),JsonValueKind.True=>true,JsonValueKind.False=>false,_=>null
        };
        return JsonSerializer.SerializeToElement(Map(element),J.Options);
    }
    public VehicleTimeline Vehicle(string id,long cursor,long generation,bool retrospect=false)
    {
        var definition=Population.SingleOrDefault(v=>v.Id==id)??throw new KeyNotFoundException("Vehicle is not in this population.");
        var scene=Scene(cursor,generation); var time=scene.CursorMs;
        var history=events.Where(e=>e.Vehicle==id && e.TimeMs<=time).Select(Entry).ToArray();
        var decisions=traces.Where(t=>t.Type=="Decision" && t.TimeMs<=time && t.Payload.GetProperty("context").Long("sourceSequence")<=scene.SourceSequence && t.Payload.GetProperty("context").Text("vehicleId")==id).Select(t=>Decision(t, time,retrospect)).ToArray();
        var route=Route(id,time);
        return new(Source.Key,generation,time,definition,scene.Vehicles.Single(v=>v.Id==id),history,decisions,route,
            ["Motion is observed every telemetry interval; traffic standstill between observations is not inferred.","Exact lane position is unavailable in this archive.","Connecting includes only the maneuvering/connection breakdown explicitly present at ChargingStarted."],scene.Source);
    }
    public string[] Route(string id,long time)=>traces.LastOrDefault(t=>t.Type=="APPLIED" && t.TimeMs<=time && t.Payload.Text("vehicleId")==id)?.Payload.Get("actualRoute")?.Items().Select(v=>v.GetString()!).ToArray()??[];
    private DecisionDetail Decision(RecordedTrace trace,long cursor,bool retrospect)
    {
        var p=trace.Payload;var context=p.GetProperty("context");var plan=context.Text("planId");var id=context.Text("vehicleId");
        economics.TryGetValue(plan,out var econ);
        var selected=econ.ValueKind==JsonValueKind.Object?econ.Text("executedParkId",p.Text("selectedParkId")):p.Text("selectedParkId");
        var reason=econ.ValueKind==JsonValueKind.Object?econ.Text("reason"):p.Text("reason");
        var execution=traces.Where(t=>t.TimeMs<=cursor && t.Type is "APPLIED" or "APPLYING" or "REJECTED" or "FAILED" && t.Payload.Text("planId")==plan).Select(Entry).ToArray();
        var outcomes=traces.Where(t=>t.Type=="Outcome" && t.Payload.Text("planId")==plan && (retrospect || t.TimeMs<=cursor)).Select(Entry).ToArray();
        var explanation=$"The recorded decision selected {(selected.Length==0?"no new park":selected)}. Recorded decision reason: {reason}. " +
            "Candidate exclusions, forecasts and score components below are the original trace; they have not been recomputed.";
        return new(context.Text("decisionId"),plan,id,trace.TimeMs,context.Long("sourceSequence"),selected,reason,explanation,
            PublicFacts(context),PublicFacts(p.Get("candidates")??J.Array),PublicFacts(p.Get("scores")??J.Array),PublicFacts(p.Get("forecast")??J.Empty),
            econ.ValueKind==JsonValueKind.Object?PublicFacts(econ):null,execution,outcomes,
            ["Decision reason is distinct from observed outcome or an investigation hypothesis.","Unvisited candidates have no observed alternative outcome.",
             retrospect?"Later outcomes are hindsight: unavailable at decision time.":"Outcomes after the cursor are hidden.","Tie-breakers are recorded policy rules; the UI does not infer a new ranking."]);
    }
    public CostReconciliationView Costs(string vehicle,long cursor,long generation,bool retrospect)
    {
        if(!Population.Any(v=>v.Id==vehicle)) throw new KeyNotFoundException("Unknown vehicle.");
        var time=Math.Clamp(cursor,0,ProducerTimeMs);var terminal=Overview.Status is not ("running" or "initializing");
        var reportHorizon=Overview.Status=="completed"?Scenario.DurationSeconds*1000L:ProducerTimeMs;
        var canSeeReport=financialReport is not null && (retrospect || terminal && time>=reportHorizon);
        JsonElement? assessment=null;JsonElement[] sessions=[],reconciliation=[];
        var availability="No financial report available at this cursor";
        if(canSeeReport)
        {
            var report=financialReport!.Value;
            assessment=report.GetProperty("vehicles").Items().Where(v=>v.Text("vehicleId")==vehicle).Select(v=>(JsonElement?)v).FirstOrDefault();
            sessions=report.GetProperty("sessions").Items().Where(v=>v.GetProperty("observation").Text("vehicleId")==vehicle).ToArray();
            reconciliation=report.GetProperty("reconciliation").Items().Where(v=>v.Text("vehicleId")==vehicle).ToArray();
            availability=report.Text("runStatus")=="completed"?"Authoritative financial report":"INCOMPLETE_RUN — known accrued amounts only";
        }
        else if(File.Exists(Path.Combine(Source.Directory,"cost-accounting.sqlite")))
        {
            // A read-only SQLite snapshot reads already committed Money values. It never
            // constructs CostJournal (whose constructor writes), resolves tariffs, or bills.
            using var db=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=Path.Combine(Source.Directory,"cost-accounting.sqlite"),
                Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1}.ToString());db.Open();
            using var command=db.CreateCommand();command.CommandText="SELECT json FROM sessions WHERE json_extract(json,'$.observation.vehicleId')=$vehicle AND json_extract(json,'$.observation.observedMs')<=$cursor ORDER BY id";
            command.Parameters.AddWithValue("$vehicle",vehicle);command.Parameters.AddWithValue("$cursor",time);
            using var rows=command.ExecuteReader();var values=new List<JsonElement>();
            while(rows.Read()){using var document=JsonDocument.Parse(rows.GetString(0));values.Add(document.RootElement.Clone());}sessions=values.ToArray();
            availability=sessions.Length==0?"Historical accrued amounts unavailable; open explicit hindsight for the final report":
                terminal && Overview.Status!="completed"?"INCOMPLETE_RUN — committed accrued amounts, no final claim":"Committed journal amounts at or before cursor";
        }
        return new(Source.Key,generation,vehicle,time,retrospect,availability,assessment,sessions,reconciliation,
            "Selected vehicle only. Fleet and paired-run totals are shown separately in Compare.",
            ["EUR NET / SYNTHETIC. Money is copied from the authoritative .NET Decimal report or committed journal.",
             "Battery inventory adjustment is internal valuation, not a provider refund.","Open or unresolved sessions are not free and are not final invoices.",
             canSeeReport && time<ProducerTimeMs?"Hindsight report: later observed results were unavailable at this cursor.":"Future final amounts are hidden unless hindsight is explicitly enabled."],
            new(canSeeReport?"economic-report.json":"cost-accounting.sqlite (read-only)",canSeeReport?financialReport!.Value.Long("sourceRecords"):0,time));
    }
    public LearningView Learning(long cursor,bool retrospect)
    {
        cursor=Math.Clamp(cursor,0,ProducerTimeMs);
        var rows=new List<LearningRow>();
        foreach(var realized in traces.Where(t=>t.Type=="RealizedDecision" && t.TimeMs<=cursor))
        {
            var p=realized.Payload;var context=p.GetProperty("context");var plan=context.Text("planId");
            var decision=traces.FirstOrDefault(t=>t.Type=="Decision" && t.Payload.GetProperty("context").Text("planId")==plan);
            var park=p.Get("candidate")?.Text("parkId")??"";
            var forecast=decision?.Payload.Get("forecast")?.Get("forecasts")?.Items().FirstOrDefault(f=>f.Text("parkId")==park)??default;
            var outcome=traces.FirstOrDefault(t=>t.Type=="Outcome" && t.Payload.Text("planId")==plan && (retrospect || t.TimeMs<=cursor));
            var label=outcome?.Payload.Get("outcome");
            rows.Add(new(context.Text("vehicleId"),realized.TimeMs,forecast.Long("revision"),forecast.Text("method","unavailable"),forecast.Get("waitSeconds") is { ValueKind:JsonValueKind.Number } prediction?prediction.GetDouble():null,
                outcome?.TimeMs,label?.Get("waitingSeconds") is { ValueKind:JsonValueKind.Number } n?n.GetDouble():null,label?.Text("status","PENDING")??"PENDING"));
        }
        JsonElement? report=null;var path=Source.StateDirectory is null?null:Path.Combine(Source.StateDirectory,"prearrival-report.json");
        if(path is not null && File.Exists(path) && (retrospect || cursor>=Scenario.DurationSeconds*1000L && Overview.Status=="completed"))report=J.Read(path);
        var policy=J.Read(Path.Combine(Source.Directory,"manifest.json")).Get("chargingPlanning")?.Text("policy","unavailable")??"unavailable";
        var planningPath=Path.Combine(Source.Directory,"planning-config.json");
        var strategy=File.Exists(planningPath)?J.Read(planningPath).Text("learningStrategy"):"";
        var multiDay=MultiDayLearning(cursor);
        var frozen=strategy.Length>0?strategy=="M0":policy=="P2";
        var observed=rows.Count(r=>r.LabelSeconds is not null);
        return new(strategy.Length>0?strategy:policy,frozen,report?.Long("initialRevision"),report?.Long("revision")??rows.Select(r=>(long?)r.Revision).LastOrDefault(),
            frozen?0:report?.Long("onlineUpdates"),observed,0,report is not null && observed>0?"Final recorded metrics (hindsight if before horizon)":"N/A — final metrics unavailable at cursor",
            report?.Get("metrics"),rows.ToArray(),multiDay is null?"N/A — no drift alarm stream in this pre-arrival profile":"Recorded alarms below are unconfirmed change signals; promotion is a separate event.",
            ["Truck pre-arrival outcomes only. Cars have no labels in this feature profile: N/A, count 0.","Prediction time and label availability are different times.",
             frozen?"Frozen development checkpoint; no online updates occur in this run.":"Update count is shown only when supplied by the recorded provider report."],multiDay);
    }

    private JsonElement? MultiDayLearning(long cursor)
    {
        if(Scenario.MultiDay is null || Source.StateDirectory is null)return null;
        var path=Path.Combine(Source.StateDirectory,"prearrival.sqlite");
        if(!File.Exists(path))return null;
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=path,Mode=SqliteOpenMode.ReadOnly,
            Pooling=false,DefaultTimeout=1}.ToString());db.Open();
        using var exists=db.CreateCommand();exists.CommandText="SELECT count(*) FROM sqlite_master WHERE type='table' AND name='md_labels'";
        if(Convert.ToInt32(exists.ExecuteScalar())==0)return null;
        JsonElement[] Read(string table)
        {
            using var command=db.CreateCommand();
            // The table name is selected locally, never from an HTTP argument.
            // The temporal filter hides future labels and promotions even when
            // replaying a completely recorded five-day experiment.
            command.CommandText=$"SELECT payload FROM {table} WHERE json_extract(payload,'$.timeMs')<=$cursor ORDER BY position LIMIT 5000";
            command.Parameters.AddWithValue("$cursor",cursor);
            using var reader=command.ExecuteReader();var values=new List<JsonElement>();
            while(reader.Read()){using var document=JsonDocument.Parse(reader.GetString(0));values.Add(document.RootElement.Clone());}
            return values.ToArray();
        }
        var labels=Read("md_labels");var transitions=Read("md_transitions");
        var days=Enumerable.Range(1,Scenario.MultiDay.Days).Select(day=>
        {
            var resolved=labels.Where(l=>l.Long("day")==day).ToArray();
            var dailyEvents=events.Where(e=>e.TimeMs<=cursor && e.TimeMs/86400000+1==day).ToArray();
            return new { day,labels=resolved.Length,maeSeconds=resolved.Length==0?(double?)null:resolved.Average(l=>Math.Abs(l.Num("prediction")-l.Num("target"))),
                revision=resolved.Length==0?(long?)null:resolved[^1].Long("currentRevision"),
                arrived=dailyEvents.Count(e=>e.Type=="VehicleReachedDestination"),stranded=dailyEvents.Count(e=>e.Type=="VehicleStranded"),
                capacityChanges=dailyEvents.Count(e=>e.Type=="ChargingParkCapacityChanged"),carLabels=0,carMaeSeconds=(double?)null };
        });
        var operatingChanges=events.Where(e=>e.Type=="ChargingParkCapacityChanged" && e.TimeMs<=cursor).Select(e=>
            new {timeMs=e.TimeMs,park=e.Vehicle,availableGridKw=e.Payload.Num("availableGridKw"),meaning="Observed operating-regime change"});
        return JsonSerializer.SerializeToElement(new { days,transitions,operatingChanges,observedThroughMs=cursor,
            interpretation="Resolved original pre-arrival predictions only. Future days and passenger-car errors remain N/A. An alarm is not proof of adaptation benefit." },J.Options);
    }
    public long? NextEvent(long cursor,string? vehicle=null)=>events.Where(e=>e.TimeMs>cursor && (vehicle is null || e.Vehicle==vehicle)).Select(e=>(long?)e.TimeMs).FirstOrDefault();
    public JsonElement[] PublishedTariffs(string park,long cursor)=>economicInputs?.Get("tariffs")?.Get("tariffs")?.Items()
        .Where(t=>t.Long("publishedAtMs")<=cursor && t.Get("applicableParks")?.Items().Any(p=>p.GetString()==park)==true).ToArray()??[];
    internal JsonElement? FinancialReport=>financialReport;
    internal (long Time,string Facts)[] Lifecycle(string? vehicle=null)=>events.Where(e=>vehicle is null || e.Vehicle==vehicle)
        .Select(e=>(e.TimeMs,$"{e.Vehicle}|{e.Type}|{e.Payload.GetRawText()}" )).ToArray();
    public Dictionary<string,string> ComparisonIdentity()
    {
        var input=J.Read(Path.Combine(Source.Directory,"external-inputs.json"));var manifest=J.Read(Path.Combine(Source.Directory,"manifest.json"));
        string Hash(JsonElement? value)=>value is null?"NONE":CFIL.Infrastructure.JsonDefaults.Hash(value.Value.GetRawText());
        return new() { ["experimentGroup"]=Source.Group,["population"]=input.Text("populationHash"),["scenarioAndDisturbances"]=input.Text("scenarioHash"),
            ["network"]=manifest.Get("traffic")?.Text("networkHash")??"",["code"]=manifest.Text("sourceHash"),
            ["initialModel"]=manifest.Get("chargingPlanning")?.Text("initialModelHash")??"NONE",["tariffs"]=Hash(economicInputs?.Get("tariffs")),
            ["fleetProfiles"]=Hash(economicInputs?.Get("fleets")),["assignments"]=economicInputs?.Text("assignmentHash")??"NONE" };
    }
}

public static class ComparisonReader
{
    public static PairComparison Compare(RunProjection left,RunProjection right,long? cursor=null,bool retrospect=false)
    {
        var a=left.ComparisonIdentity();var b=right.ComparisonIdentity();
        var reasons=a.Where(p=>string.IsNullOrEmpty(p.Value) || p.Value!=b.GetValueOrDefault(p.Key)).Select(p=>"Different or unavailable "+p.Key).ToArray();
        JsonElement? quality=left.Source.QualityPath is { } path && File.Exists(path)?J.Read(path):null;
        var money=new Dictionary<string,Money>();var operation=new Dictionary<string,decimal>();var different=new List<string>();long? earliest=null;
        var finalVisible=cursor is null || retrospect || cursor>=Math.Max(left.Scenario.DurationSeconds,right.Scenario.DurationSeconds)*1000L;
        if(reasons.Length==0 && finalVisible && left.FinancialReport is { } l && right.FinancialReport is { } r)
        {
            // Same Money type and Decimal representation as the accepted financial
            // reports. This is subtraction of observed reports, never re-pricing.
            foreach(var p in l.GetProperty("truckTotals").EnumerateObject())
                if(p.Value.Get("amount") is not null) money[p.Name]=ReadMoney(p.Value)-ReadMoney(r.GetProperty("truckTotals").GetProperty(p.Name));
            foreach(var v in l.GetProperty("vehicles").Items())
            {
                var other=r.GetProperty("vehicles").Items().Single(x=>x.Text("vehicleId")==v.Text("vehicleId"));
                if(v.GetRawText()!=other.GetRawText()) different.Add(v.Text("vehicleId"));
            }
        }
        if(reasons.Length==0 && finalVisible)
        {
            foreach(var key in new[]{"arrived","punctual","stranded","unsuccessful","incomplete"})
            {
                if(left.Source.ResultPath is not { } lp || right.Source.ResultPath is not { } rp || !File.Exists(lp) || !File.Exists(rp))continue;
                var lm=Operational(left,lp);var rm=Operational(right,rp);
                if(lm.Get(key) is {} lv && rm.Get(key) is {} rv)operation[key]=lv.GetDecimal()-rv.GetDecimal();
            }
            var le=left.Lifecycle();var re=right.Lifecycle();var i=0;
            while(i<Math.Min(le.Length,re.Length) && le[i]==re[i])i++;
            if(i<le.Length || i<re.Length)earliest=Math.Min(i<le.Length?le[i].Time:long.MaxValue,i<re.Length?re[i].Time:long.MaxValue);
        }
        return new(reasons.Length==0,reasons,left.Source.Key,right.Source.Key,left.Population.Count(v=>v.Kind==VehicleKind.Truck),
            $"Left minus right, total over all {left.Population.Count(v=>v.Kind==VehicleKind.Truck)} planned trucks in this paired run. Not per truck.",money,operation,quality,
            "Exploratory selected seed. The formal five-seed quality verdict below is unchanged by this selection.",different.ToArray(),earliest,
            "First difference in recorded lifecycle events; coalesced power telemetry is excluded. Temporal order is not proof of cause.");
    }
    public static Money ReadMoney(JsonElement value)=>new(decimal.Parse(value.Text("amount"),CultureInfo.InvariantCulture),value.Text("currency","EUR"));
    private static JsonElement Operational(RunProjection projection,string result)
    {
        if(J.Read(result).Get("operational")?.Get("byKind")?.Get("Truck") is {} economicRun)return economicRun;
        // CFIL04 did not embed the later CFIL05 operational export in its launcher
        // result. Read the original producer metrics; absent fields remain N/A.
        var path=Path.Combine(projection.Source.Directory,"metrics.json");
        return File.Exists(path)?J.Read(path).GetProperty("byKind").Items().FirstOrDefault(v=>v.Text("kind")=="Truck"):J.Empty;
    }
}
