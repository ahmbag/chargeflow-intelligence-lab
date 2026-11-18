using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CFIL.Domain;
using CFIL.Simulation;

namespace CFIL.Infrastructure;

public static class EconomicJson
{
    private sealed class DecimalText : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String ? decimal.Parse(reader.GetString()!, CultureInfo.InvariantCulture) : reader.GetDecimal();
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("0.############################", CultureInfo.InvariantCulture));
    }
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var options=new JsonSerializerOptions(JsonDefaults.Options); options.Converters.Insert(0,new DecimalText()); return options;
    }
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value,Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value,Options) ?? throw new InvalidDataException("Null financial document.");
    public static void Write<T>(string path,T value) => File.WriteAllText(path,JsonSerializer.Serialize(value,new JsonSerializerOptions(Options){WriteIndented=true}));
}

public sealed record EconomicHeader(string Version, string RunId, EconomicOptions Options, TariffCatalog Tariffs,
    FleetEconomicCatalog Fleets, EconomicAssignment[] Assignments, string AssignmentHash, VehicleDefinition[] Population, ChargingPark[] Parks);
public sealed record CostSourceRecord(string Version,string RunId,long Sequence,long TimeMs,long PhysicsSequence,string Type,JsonElement Payload);
public sealed record SessionMeterObservation(string SessionId,string AttemptId,string VehicleId,string ParkId,string BayId,
    string Connector,long? StartedMs,long? EndedMs,long? ReleasedMs,long ObservedMs,decimal GridEnergyKwh,
    decimal BatteryEnergyKwh,decimal ExpectedGridKwhAtStart,long ExpectedChargeMsAtStart,string PhysicalOutcome,
    CostRecordStatus LifecycleStatus);
public sealed record TripCostObservation(string VehicleId,string Kind,string Status,long DepartureMs,long? EnteredMs,
    long? ArrivedMs,long ObservedMs,long TargetArrivalMs,decimal InitialEnergyKwh,decimal EndEnergyKwh,
    decimal DistanceKm,decimal DirectRouteKm,decimal ConsumedBatteryKwh);

public sealed class EconomicService : IEconomicPlanningLayer, IEconomicObservationSink, IAsyncDisposable
{
    private readonly EconomicHeader header;
    private readonly TariffResolver resolver;
    private readonly StreamWriter source;
    private readonly CostJournal journal;
    private readonly Dictionary<string,EconomicDecision> proposals=[];
    private readonly Dictionary<string,(long? Start,long? End,long? Released,string Outcome)> observed=[];
    private readonly Dictionary<string,(decimal Energy,long Duration)> expected=[];
    private long sequence;
    public EconomicHeader Header => header;
    public double MaximumProcessingSeconds { get; private set; }
    public object? MaximumProcessingDiagnostic { get; private set; }

    public EconomicService(string output, SimulationRun run, EconomicOptions settings, VehicleDefinition[] population, ChargingPark[] parks)
    {
        var tariffs=EconomicJson.Read<TariffCatalog>(File.ReadAllText(settings.TariffsPath));
        var fleets=EconomicJson.Read<FleetEconomicCatalog>(File.ReadAllText(settings.ProfilesPath));
        resolver=new(tariffs);
        if (fleets.Version!="FLEET-ECONOMICS-V1" || fleets.Profiles.Length<2 ||
            fleets.Profiles.Select(p=>p.Id).Distinct().Count()!=fleets.Profiles.Length || fleets.Profiles.Any(p=>
                string.IsNullOrWhiteSpace(p.Version) || p.TimeValuePerHour<0 || p.NonEnergyDistanceCostPerKm<0 || p.IncrementalLateCostPerHour<0 ||
                p.AllowedPaymentRoutes.Any(r=>!tariffs.PaymentRoutes.Any(x=>x.Id==r)))) throw new ArgumentException("Invalid fleet economic profiles.");
        var profiles=fleets.Profiles.OrderBy(p=>p.Id,StringComparer.Ordinal).ToArray();
        var assignments=population.OrderBy(v=>v.Id,StringComparer.Ordinal).Select((v,i)=>new EconomicAssignment(v.Id,
            profiles[i%profiles.Length].Id,new(v.Id,profiles[i%profiles.Length].Id,"ORDER-"+v.Id,
                v.Logistics?.TargetArrivalMs ?? 0,"SIM-ORDER/"+v.Id))).ToArray();
        header=new("ECONOMIC-SOURCE-V1",run.RunId,settings,tariffs,fleets,assignments,
            JsonDefaults.Hash(EconomicJson.Serialize(assignments)),population,parks);
        EconomicJson.Write(Path.Combine(output,"economic-config.json"),settings);
        EconomicJson.Write(Path.Combine(output,"economic-inputs.json"),header);
        source=new(new FileStream(Path.Combine(output,"cost-observations.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false));
        journal=new(Path.Combine(output,"cost-accounting.sqlite"));
        Append("Header",header,0,0);
    }

    public FleetEconomicProfile Profile(string vehicleId) => header.Fleets.Profiles.Single(p=>p.Id==header.Assignments.Single(a=>a.VehicleId==vehicleId).ProfileId);
    public static Dictionary<string,string[]> CompatibleConnectors(EconomicHeader header,PlanProposal proposal) =>
        proposal.Context.Parks.ToDictionary(p=>p.Id,p=>header.Parks.Single(s=>s.Id==p.Id).Bays
            .Where(b=>p.CompatibleBayIds.Contains(b.Id)).Select(b=>b.Connector.Type).Distinct().ToArray());
    private void Append(string type,object payload,long now,long physicsSequence)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();
        var record=new CostSourceRecord("ECONOMIC-SOURCE-V1",header.RunId,++sequence,now,physicsSequence,type,
            JsonSerializer.SerializeToElement(payload,EconomicJson.Options));
        var line=EconomicJson.Serialize(record);
        var serializedSeconds=timer.Elapsed.TotalSeconds;
        // Durable source first, then an atomic projection transaction. If the process
        // dies between the two, replay consumes this very same record, never a quote.
        source.WriteLine(line); source.Flush(); ((FileStream)source.BaseStream).Flush(true);
        var durableSourceSeconds=timer.Elapsed.TotalSeconds;
        journal.Apply(record,line,source.BaseStream.Position);
        var elapsed=timer.Elapsed.TotalSeconds;
        if (elapsed>MaximumProcessingSeconds)
        {
            MaximumProcessingSeconds=elapsed;
            // Attribute storage stalls without adding a diagnostic write to the
            // measured path. These wall-clock values never enter financial reports.
            MaximumProcessingDiagnostic=new { type,record.Sequence,serializationSeconds=serializedSeconds,
                sourceFlushSeconds=durableSourceSeconds-serializedSeconds,journalSeconds=elapsed-durableSourceSeconds };
        }
    }

    public Task<PlanProposal> EvaluateAsync(PlanProposal proposal,Vehicle vehicle,PlanningOptions planning,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (options,unpriced,budget)=EconomicPlanningRules.Options(proposal,vehicle.Definition,Profile(vehicle.Id),resolver,header.Options,CompatibleConnectors(header,proposal));
        var (recommended,reason)=EconomicPlanningRules.Select(proposal,options,header.Options,planning);
        var executed=header.Options.Mode==EconomicMode.ECONOMIC_CONTROLLED?recommended:proposal.SelectedParkId;
        var decision=new EconomicDecision(proposal.Context.PlanId,vehicle.Id,proposal.Context.TimeMs,proposal.Context.SourceSequence,
            header.Options.Strategy.ToString(),header.Options.Mode,proposal.SelectedParkId,recommended,executed,reason,options,unpriced,
            (decimal)vehicle.DistanceM,budget);
        proposals.Add(decision.PlanId,decision);
        Append("Decision",decision,proposal.Context.TimeMs,proposal.Context.SourceSequence);
        return Task.FromResult(header.Options.Mode==EconomicMode.ECONOMIC_CONTROLLED
            ? proposal with { SelectedParkId=executed,Reason=reason } : proposal);
    }

    public Task RealizedAsync(PlanProposal proposal,string attemptId,string actualPark,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var decision=proposals[proposal.Context.PlanId];
        Append("Realized",new RealizedEconomicDecision(attemptId,decision,decision.Options.SingleOrDefault(o=>o.ParkId==actualPark)),
            proposal.Context.TimeMs,proposal.Context.SourceSequence);
        return Task.CompletedTask;
    }

    public Task ObserveAsync(long nowMs,long sourceSequence,IReadOnlyDictionary<string,Vehicle> vehicles,
        IReadOnlyList<ChargingSession> sessions,IReadOnlyList<ChargingParkState> parks,bool final,string runStatus,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var session in sessions)
        {
            var state=(session.StartedMs,session.EndedMs,session.ReleasedMs,session.Outcome);
            var changed=!observed.TryGetValue(session.Id,out var previous) || previous!=state;
            if (!changed && !final && (session.ReleasedMs is not null || nowMs%60000!=0)) continue;
            observed[session.Id]=state;
            var v=vehicles[session.VehicleId]; var park=parks.Single(p=>p.Definition.Id==session.ParkId);
            var bay=park.Definition.Bays.Single(b=>b.Id==session.BayId);
            if (session.StartedMs is not null && !expected.ContainsKey(session.Id))
            {
                var grid=ChargingCostCalculator.Meter(Math.Max(0,v.Definition.Energy.TargetSoc*v.Definition.CapacityKwh-v.Battery.EnergyKwh)/v.Definition.Energy.Efficiency);
                var power=Math.Max(.001,Math.Min(bay.Connector.MaxGridPowerKw,park.AvailableGridKw/Math.Max(1,park.BayOccupants.Count)));
                var duration=ChargingPlanningRules.ChargingSeconds(v.Definition.Energy,v.Definition.CapacityKwh,v.Battery.Soc,v.Definition.Energy.TargetSoc,power);
                expected.Add(session.Id,(grid,(long)Math.Ceiling(duration*1000)));
            }
            var estimate=expected.GetValueOrDefault(session.Id);
            var status=runStatus is not ("running" or "completed")?CostRecordStatus.INCOMPLETE_RUN:
                session.EndedMs is not null && session.ReleasedMs is not null?CostRecordStatus.FINAL:CostRecordStatus.OPEN_AT_HORIZON;
            Append("Session",new SessionMeterObservation(session.Id,session.DecisionId,session.VehicleId,session.ParkId,session.BayId,
                bay.Connector.Type,session.StartedMs,session.EndedMs,session.ReleasedMs,nowMs,ChargingCostCalculator.Meter(session.GridEnergyKwh),
                ChargingCostCalculator.Meter(session.BatteryEnergyKwh),estimate.Energy,estimate.Duration,session.Outcome,status),nowMs,sourceSequence);
        }
        if (final)
        {
            foreach (var v in vehicles.Values.OrderBy(v=>v.Id,StringComparer.Ordinal))
                Append("Trip",new TripCostObservation(v.Id,v.Definition.Kind.ToString(),v.Status.ToString(),v.Definition.DepartureSeconds*1000L,
                    v.EnteredMs,v.ArrivedMs,nowMs,v.Definition.Logistics?.TargetArrivalMs??0,
                    ChargingCostCalculator.Meter(v.Definition.InitialSoc*v.Definition.CapacityKwh),ChargingCostCalculator.Meter(v.Battery.EnergyKwh),
                    (decimal)v.DistanceM/1000,(decimal)v.Definition.RouteEndKm,ChargingCostCalculator.Meter(v.ConsumedKwh)),nowMs,sourceSequence);
            Append("Completed",new { status=runStatus,maximumProcessingSeconds=MaximumProcessingSeconds,
                maximumProcessingDiagnostic=MaximumProcessingDiagnostic },nowMs,sourceSequence);
        }
        else if (nowMs%3600000==0)
        {
            // Operational diagnostics stay outside the immutable business streams.
            // A long resource qualification can detect storage stalls before completion.
            Console.Error.WriteLine("Economic journal: "+EconomicJson.Serialize(new {
                maximumProcessingSeconds=MaximumProcessingSeconds,maximumProcessingDiagnostic=MaximumProcessingDiagnostic }));
        }
        return Task.CompletedTask;
    }
    public void Export(string output) => journal.Export(output);
    public ValueTask DisposeAsync() { source.Dispose(); journal.Dispose(); return ValueTask.CompletedTask; }
}
