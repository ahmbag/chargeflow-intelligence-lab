using System.Text;
using System.Text.Json;
using CFIL.Domain;
using CFIL.Simulation;
using Microsoft.Data.Sqlite;

namespace CFIL.Infrastructure;

public sealed record AccountedSession(SessionMeterObservation Observation, PricingSnapshot? BoundPricing,
    string PricingStatus, CostRecordStatus Status, ChargingBill? ActualAccruedBill, EconomicOption? OriginalEstimate,
    string PriceBindingDifference, Money? BillVariance);

public sealed class CostJournal : IDisposable
{
    private readonly SqliteConnection db;
    private EconomicHeader? header;
    public Action<string,long>? FaultHook { get; set; }
    public CostJournal(string path)
    {
        db=new(new SqliteConnectionStringBuilder { DataSource=path,Pooling=false }.ToString()); db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;" +
            "CREATE TABLE IF NOT EXISTS source_records(sequence INTEGER PRIMARY KEY, hash TEXT NOT NULL, end_offset INTEGER NOT NULL);"+
            "CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);"+
            "CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,json TEXT NOT NULL);"+
            "CREATE TABLE IF NOT EXISTS decisions(id TEXT PRIMARY KEY,json TEXT NOT NULL);"+
            "CREATE TABLE IF NOT EXISTS recommendations(id TEXT PRIMARY KEY,json TEXT NOT NULL);"+
            "CREATE TABLE IF NOT EXISTS trips(id TEXT PRIMARY KEY,json TEXT NOT NULL);"+
            "CREATE TABLE IF NOT EXISTS checkpoint(id INTEGER PRIMARY KEY CHECK(id=1),sequence INTEGER NOT NULL,end_offset INTEGER NOT NULL);"+
            "INSERT OR IGNORE INTO checkpoint VALUES(1,0,0);");
        var saved=Scalar("SELECT value FROM metadata WHERE key='header'");
        if (saved is not null) header=EconomicJson.Read<EconomicHeader>(saved);
    }
    public long Sequence => long.Parse(Scalar("SELECT sequence FROM checkpoint WHERE id=1")!,System.Globalization.CultureInfo.InvariantCulture);
    public long EndOffset => long.Parse(Scalar("SELECT end_offset FROM checkpoint WHERE id=1")!,System.Globalization.CultureInfo.InvariantCulture);
    private void Execute(string sql,SqliteTransaction? transaction=null,params (string Key,object Value)[] args)
    {
        using var cmd=db.CreateCommand(); cmd.Transaction=transaction; cmd.CommandText=sql;
        foreach (var (key,value) in args) cmd.Parameters.AddWithValue(key,value);
        cmd.ExecuteNonQuery();
    }
    private string? Scalar(string sql,SqliteTransaction? transaction=null,params (string Key,object Value)[] args)
    {
        using var cmd=db.CreateCommand(); cmd.Transaction=transaction; cmd.CommandText=sql;
        foreach (var (key,value) in args) cmd.Parameters.AddWithValue(key,value);
        return Convert.ToString(cmd.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture) is { Length:>0 } text?text:null;
    }
    private void Put(string table,string id,object value,SqliteTransaction transaction) => Execute(
        $"INSERT INTO {table}(id,json) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json",transaction,
        ("$id",id),("$json",EconomicJson.Serialize(value)));
    private T[] Rows<T>(string table)
    {
        using var cmd=db.CreateCommand(); cmd.CommandText=$"SELECT json FROM {table} ORDER BY id";
        using var rows=cmd.ExecuteReader(); var result=new List<T>();
        while (rows.Read()) result.Add(EconomicJson.Read<T>(rows.GetString(0)));
        return result.ToArray();
    }
    public void Apply(CostSourceRecord record,string raw,long endOffset)
    {
        var previousHeader=header;
        using var transaction=db.BeginTransaction();
        try
        {
            ApplyCore(record,raw,endOffset,transaction);
            FaultHook?.Invoke("before-commit",record.Sequence);
            transaction.Commit();
        }
        catch { header=previousHeader; throw; }
        FaultHook?.Invoke("after-commit",record.Sequence);
    }

    private void ApplyBatch(IReadOnlyList<(CostSourceRecord Record,string Raw,long EndOffset)> records)
    {
        var previousHeader=header;
        using var transaction=db.BeginTransaction();
        try
        {
            // Offline replay groups durable-source records into one transaction.
            // Projection, journal and watermark still commit together; after a crash
            // the entire uncommitted batch is replayed. Live ingestion stays per-record.
            foreach (var item in records) ApplyCore(item.Record,item.Raw,item.EndOffset,transaction);
            FaultHook?.Invoke("before-batch-commit",records[^1].Record.Sequence);
            transaction.Commit();
        }
        catch { header=previousHeader; throw; }
        FaultHook?.Invoke("after-batch-commit",records[^1].Record.Sequence);
    }

    private void ApplyCore(CostSourceRecord record,string raw,long endOffset,SqliteTransaction transaction)
    {
        if (record.Version!="ECONOMIC-SOURCE-V1" || record.Sequence<1 || record.TimeMs<0) throw new InvalidDataException("Invalid financial source envelope.");
        var hash=JsonDefaults.Hash(raw);
        var duplicate=Scalar("SELECT hash FROM source_records WHERE sequence=$seq",transaction,("$seq",record.Sequence));
        if (duplicate is not null)
        {
            var committedOffset=Scalar("SELECT end_offset FROM source_records WHERE sequence=$seq",transaction,("$seq",record.Sequence));
            if (duplicate!=hash || committedOffset!=endOffset.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidDataException("Conflicting financial event duplicate or source byte position.");
            return;
        }
        var checkpoint=long.Parse(Scalar("SELECT sequence FROM checkpoint WHERE id=1",transaction)!,System.Globalization.CultureInfo.InvariantCulture);
        var previousOffset=long.Parse(Scalar("SELECT end_offset FROM checkpoint WHERE id=1",transaction)!,System.Globalization.CultureInfo.InvariantCulture);
        if (record.Sequence!=checkpoint+1 || endOffset<=previousOffset || header is not null && record.RunId!=header.RunId)
            throw new InvalidDataException("Financial source gap, offset regression or run mismatch.");
        if (Scalar("SELECT value FROM metadata WHERE key='completion'",transaction) is not null)
            throw new InvalidDataException("Financial source continued after terminal completion.");
        var lastTime=long.Parse(Scalar("SELECT value FROM metadata WHERE key='last_time_ms'",transaction)??"0",System.Globalization.CultureInfo.InvariantCulture);
        var lastPhysics=long.Parse(Scalar("SELECT value FROM metadata WHERE key='last_physics_sequence'",transaction)??"0",System.Globalization.CultureInfo.InvariantCulture);
        if (record.TimeMs<lastTime || record.PhysicsSequence<lastPhysics) throw new InvalidDataException("Financial source clock or physics position regressed.");
        var payload=record.Payload.GetRawText();
        switch (record.Type)
        {
            case "Header":
                if (record.Sequence!=1 || header is not null) throw new InvalidDataException("Immutable accounting header already bound.");
                var incoming=EconomicJson.Read<EconomicHeader>(payload);
                _=new TariffResolver(incoming.Tariffs);
                if (incoming.RunId!=record.RunId || JsonDefaults.Hash(EconomicJson.Serialize(incoming.Assignments))!=incoming.AssignmentHash)
                    throw new InvalidDataException("Header identity/assignment hash mismatch.");
                Execute("INSERT INTO metadata VALUES('header',$value)",transaction,("$value",payload));
                header=incoming; break;
            case "Decision":
                var decision=EconomicJson.Read<EconomicDecision>(payload);
                if (Scalar("SELECT json FROM recommendations WHERE id=$id",transaction,("$id",decision.PlanId)) is not null)
                    throw new InvalidDataException("Duplicate recommendation identity.");
                Put("recommendations",decision.PlanId,decision,transaction); break;
            case "Realized":
                var realized=EconomicJson.Read<RealizedEconomicDecision>(payload);
                if (Scalar("SELECT json FROM decisions WHERE id=$id",transaction,("$id",realized.AttemptId)) is not null)
                    throw new InvalidDataException("Duplicate realized decision identity.");
                Put("decisions",realized.AttemptId,realized,transaction); break;
            case "Session":
                var session=EconomicJson.Read<SessionMeterObservation>(payload);
                if (session.ObservedMs!=record.TimeMs) throw new InvalidDataException("Session source clock mismatch.");
                Account(session,transaction); break;
            case "Trip":
                var trip=EconomicJson.Read<TripCostObservation>(payload);
                var oldTrip=Scalar("SELECT json FROM trips WHERE id=$id",transaction,("$id",trip.VehicleId));
                if (oldTrip is not null && oldTrip!=EconomicJson.Serialize(trip)) throw new InvalidDataException("Conflicting final trip observation.");
                Put("trips",trip.VehicleId,trip,transaction); break;
            case "Completed":
                if (Scalar("SELECT value FROM metadata WHERE key='completion'",transaction) is not null) throw new InvalidDataException("Repeated accounting completion.");
                Execute("INSERT INTO metadata VALUES('completion',$value)",transaction,("$value",payload)); break;
            default: throw new InvalidDataException("Unknown financial event type.");
        }
        Execute("INSERT INTO source_records VALUES($seq,$hash,$offset)",transaction,("$seq",record.Sequence),("$hash",hash),("$offset",endOffset));
        Execute("UPDATE checkpoint SET sequence=$seq,end_offset=$offset WHERE id=1",transaction,("$seq",record.Sequence),("$offset",endOffset));
        Execute("INSERT INTO metadata VALUES('last_time_ms',$time) ON CONFLICT(key) DO UPDATE SET value=excluded.value",transaction,("$time",record.TimeMs.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Execute("INSERT INTO metadata VALUES('last_physics_sequence',$seq) ON CONFLICT(key) DO UPDATE SET value=excluded.value",transaction,("$seq",record.PhysicsSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private void Account(SessionMeterObservation observation,SqliteTransaction transaction)
    {
        var h=header??throw new InvalidDataException("Missing immutable accounting header.");
        var old=Scalar("SELECT json FROM sessions WHERE id=$id",transaction,("$id",observation.SessionId));
        var previous=old is null?null:EconomicJson.Read<AccountedSession>(old);
        if (!h.Population.Any(v=>v.Id==observation.VehicleId) || !Enum.IsDefined(observation.LifecycleStatus) ||
            !h.Parks.Any(p=>p.Id==observation.ParkId && p.Bays.Any(b=>b.Id==observation.BayId && b.Connector.Type==observation.Connector)) ||
            observation.GridEnergyKwh<0 || observation.BatteryEnergyKwh<0 || observation.ExpectedGridKwhAtStart<0 || observation.ExpectedChargeMsAtStart<0 ||
            observation.StartedMs is null && (observation.GridEnergyKwh!=0 || observation.EndedMs is not null || observation.ReleasedMs is not null) ||
            observation.LifecycleStatus==CostRecordStatus.FINAL && (observation.EndedMs is null || observation.ReleasedMs is null) ||
            observation.BatteryEnergyKwh>observation.GridEnergyKwh+.000000001m ||
            observation.StartedMs>observation.ObservedMs || observation.EndedMs>observation.ObservedMs || observation.ReleasedMs>observation.ObservedMs ||
            observation.EndedMs<observation.StartedMs || observation.ReleasedMs<observation.EndedMs)
            throw new InvalidDataException("Invalid physical session meter or intervals.");
        if (previous is not null)
        {
            var p=previous.Observation;
            if (observation.VehicleId!=p.VehicleId || observation.AttemptId!=p.AttemptId || observation.ParkId!=p.ParkId || observation.Connector!=p.Connector ||
                observation.ObservedMs<p.ObservedMs || observation.GridEnergyKwh<p.GridEnergyKwh || observation.BatteryEnergyKwh<p.BatteryEnergyKwh ||
                p.StartedMs is not null && observation.StartedMs!=p.StartedMs || p.EndedMs is not null && observation.EndedMs!=p.EndedMs ||
                p.ReleasedMs is not null && observation.ReleasedMs!=p.ReleasedMs ||
                observation.ObservedMs==p.ObservedMs && (observation.GridEnergyKwh!=p.GridEnergyKwh || observation.BatteryEnergyKwh!=p.BatteryEnergyKwh))
                throw new InvalidDataException("Conflicting session identity, cumulative meter or lifecycle.");
        }
        var pricing=previous?.BoundPricing;
        var pricingStatus=previous?.PricingStatus??"NOT_STARTED";
        var original=previous?.OriginalEstimate;
        var difference=previous?.PriceBindingDifference??"NO_ORIGINAL_ESTIMATE";
        var calculator=new ChargingCostCalculator();
        if (observation.StartedMs is { } started && pricingStatus=="NOT_STARTED")
        {
            var realizedJson=Scalar("SELECT json FROM decisions WHERE id=$id",transaction,("$id",observation.AttemptId));
            original=realizedJson is null?null:EconomicJson.Read<RealizedEconomicDecision>(realizedJson).SelectedOption;
            var fleet=h.Fleets.Profiles.Single(p=>p.Id==h.Assignments.Single(a=>a.VehicleId==observation.VehicleId).ProfileId);
            var prices=new TariffResolver(h.Tariffs).Resolve(observation.ParkId,[observation.Connector],fleet,started,started);
            var route=original?.Estimate.EstimatedChargingBill.Pricing.PaymentRouteId;
            pricing=prices.SingleOrDefault(p=>p.PaymentRouteId==route);
            // A lost contract never grants ad-hoc rights. Fallback candidates come
            // from the very same entitlement resolver, including explicit direct access.
            pricing ??= prices.Select(p=>calculator.Calculate(p,new(observation.ExpectedGridKwhAtStart,observation.ExpectedChargeMsAtStart,0,true)))
                .OrderBy(b=>b.Total.Amount).ThenBy(b=>b.Pricing.PaymentRouteId,StringComparer.Ordinal).FirstOrDefault()?.Pricing;
            pricingStatus=pricing is null?"UNRESOLVED":"BOUND";
            difference=original is null?"NO_ORIGINAL_ESTIMATE":pricing is null?"NO_ACCESSIBLE_TARIFF_AT_SESSION_START":
                pricing.PaymentRouteId!=route?"ENTITLEMENT_OR_PAYMENT_ROUTE_CHANGED":
                pricing.TariffId!=original.Estimate.EstimatedChargingBill.Pricing.TariffId || pricing.Version!=original.Estimate.EstimatedChargingBill.Pricing.Version?
                    "TARIFF_CHANGED_BEFORE_SESSION_START":"SAME_TARIFF_VERSION";
        }
        // Zero power before ChargingCompleted is still charging time. Only the
        // explicit completion timestamp opens occupancy time, ending at bay release.
        var quantities=new BillingQuantities(observation.GridEnergyKwh,
            observation.StartedMs is { } start?Math.Max(0,(observation.EndedMs??observation.ObservedMs)-start):0,
            observation.EndedMs is { } end?Math.Max(0,(observation.ReleasedMs??observation.ObservedMs)-end):0,observation.StartedMs is not null);
        var bill=pricing is null?null:calculator.Calculate(pricing,quantities);
        var status=pricingStatus=="UNRESOLVED"?CostRecordStatus.PRICING_UNRESOLVED:observation.LifecycleStatus;
        Put("sessions",observation.SessionId,new AccountedSession(observation,pricing,pricingStatus,status,bill,original,difference,
            bill is not null && original is not null?bill.Total-original.Estimate.EstimatedChargingBill.Total:null),transaction);
    }

    public void Export(string directory)
    {
        Directory.CreateDirectory(directory);
        var h=header??throw new InvalidDataException("Missing accounting header.");
        var sessions=Rows<AccountedSession>("sessions"); var trips=Rows<TripCostObservation>("trips");
        var decisions=Rows<RealizedEconomicDecision>("decisions"); var recommendations=Rows<EconomicDecision>("recommendations");
        var completed=Scalar("SELECT value FROM metadata WHERE key='completion'");
        var runStatus=completed is null?"incomplete":JsonDocument.Parse(completed).RootElement.GetProperty("status").GetString();
        var valued=new List<object>(); var reconciliation=new List<object>();
        decimal totalBill=0,totalEnergyFees=0,totalFixed=0,totalChargingTime=0,totalOccupancy=0,totalTime=0,totalDistance=0,totalLate=0,totalInventory=0;
        foreach (var assignment in h.Assignments.Where(a=>h.Population.Single(v=>v.Id==a.VehicleId).Kind==VehicleKind.Truck))
        {
            var fleet=h.Fleets.Profiles.Single(p=>p.Id==assignment.ProfileId);
            var trip=trips.SingleOrDefault(t=>t.VehicleId==assignment.VehicleId);
            var records=sessions.Where(s=>s.Observation.VehicleId==assignment.VehicleId).ToArray();
            var bill=records.Where(s=>s.ActualAccruedBill is not null).Sum(s=>s.ActualAccruedBill!.Total.Amount);
            decimal Component(string name)=>records.Where(s=>s.ActualAccruedBill is not null).Sum(s=>s.ActualAccruedBill!.Lines.Single(l=>l.Component==name).Amount.Amount);
            totalBill+=bill; totalEnergyFees+=Component("ENERGY"); totalFixed+=Component("SESSION");
            totalChargingTime+=Component("CHARGING_TIME"); totalOccupancy+=Component("POST_CHARGE_OCCUPANCY");
            if (trip is null)
            {
                valued.Add(new { assignment.VehicleId,knownAccruedChargingBill=new Money(bill),status="MISSING_TERMINAL_OBSERVATION",costsComplete=false });
                continue;
            }
            static decimal Round(decimal value)=>decimal.Round(value,2,MidpointRounding.ToEven);
            var end=trip.ArrivedMs??trip.ObservedMs;
            var seconds=Math.Max(0,(end-trip.DepartureMs)/1000m);
            var time=Round(seconds/3600*fleet.TimeValuePerHour);
            var distance=Round(trip.DistanceKm*fleet.NonEnergyDistanceCostPerKm);
            var late=Round(Math.Max(0,(end-trip.TargetArrivalMs)/1000m)/3600*fleet.IncrementalLateCostPerHour);
            var inventory=Round((trip.InitialEnergyKwh-trip.EndEnergyKwh)*h.Options.InventoryReferenceEurPerKwh);
            totalTime+=time; totalDistance+=distance; totalLate+=late; totalInventory+=inventory;
            var priced=records.All(s=>s.PricingStatus!="UNRESOLVED");
            valued.Add(new { assignment.VehicleId,trip.Status,trip.InitialEnergyKwh,trip.EndEnergyKwh,trip.DistanceKm,
                observedExcessDistanceKm=Math.Max(0,trip.DistanceKm-trip.DirectRouteKm),valuedSeconds=seconds,
                rawChargingBill=new Money(bill),valuedTime=new Money(time),nonEnergyDistance=new Money(distance),incrementalLate=new Money(late),
                inventoryAdjustment=new Money(inventory),adjustedEconomicCost=new Money(bill+time+distance+late+inventory),
                costsComplete=priced && runStatus=="completed" && records.All(s=>s.Status==CostRecordStatus.FINAL),
                successful=trip.Status=="Arrived",punctual=trip.ArrivedMs is not null && trip.ArrivedMs<=trip.TargetArrivalMs });
        }
        foreach (var decision in decisions)
        {
            var session=sessions.SingleOrDefault(s=>s.Observation.AttemptId==decision.AttemptId);
            var trip=trips.SingleOrDefault(t=>t.VehicleId==decision.Decision.VehicleId);
            var fleet=h.Fleets.Profiles.Single(p=>p.Id==h.Assignments.Single(a=>a.VehicleId==decision.Decision.VehicleId).ProfileId);
            EconomicCostEstimate? actual=null;
            if (session?.ActualAccruedBill is { } bill && trip is not null)
                actual=new EconomicCostEvaluator().Evaluate(bill,fleet,Math.Max(0,((trip.ArrivedMs??trip.ObservedMs)-decision.Decision.TimeMs)/1000m),
                    Math.Max(0,trip.DistanceKm-decision.Decision.StartingDistanceM/1000),decision.Decision.TimeMs,trip.TargetArrivalMs);
            reconciliation.Add(new { decision.AttemptId,decision.Decision.PlanId,decision.Decision.VehicleId,
                selectedPark=decision.SelectedOption?.ParkId,sessionId=session?.Observation.SessionId,
                estimated=decision.SelectedOption?.Estimate,actualSelectedOutcome=actual,status=session?.Status.ToString()??"NO_OBSERVED_SESSION",
                session?.PriceBindingDifference,session?.BillVariance,
                economicVariance=actual is not null && decision.SelectedOption is not null?actual.EconomicScore-decision.SelectedOption.Estimate.EconomicScore:(Money?)null,
                counterfactualAlternativeCost=(Money?)null });
        }
        var unresolved=sessions.Count(s=>s.PricingStatus=="UNRESOLVED");
        var report=new { version="ECONOMIC-REPORT-V1",h.RunId,h.AssignmentHash,mode=h.Options.Mode,strategy=h.Options.Strategy,
            currency="EUR",priceBasis="NET",sourceClassification="SYNTHETIC",runStatus,
            allPlannedTrucks=h.Population.Count(v=>v.Kind==VehicleKind.Truck),observedTerminalTrucks=trips.Count(t=>t.Kind=="Truck"),
            pricingComplete=unresolved==0,unresolvedSessions=unresolved,sourceRecords=Sequence,
            sessionsFinal=sessions.Count(s=>s.Status==CostRecordStatus.FINAL),sessionsOpen=sessions.Count(s=>s.Observation.LifecycleStatus==CostRecordStatus.OPEN_AT_HORIZON),
            incompleteSessions=sessions.Count(s=>s.Observation.LifecycleStatus==CostRecordStatus.INCOMPLETE_RUN),
            truckTotals=new { rawChargingBill=new Money(totalBill),energyFees=new Money(totalEnergyFees),sessionFees=new Money(totalFixed),
                chargingTimeFees=new Money(totalChargingTime),occupancyFees=new Money(totalOccupancy),valuedTime=new Money(totalTime),
                nonEnergyDistance=new Money(totalDistance),incrementalLate=new Money(totalLate),inventoryAdjustment=new Money(totalInventory),
                adjustedEnergyComparison=new Money(totalEnergyFees+totalInventory),
                adjustedEconomicCost=new Money(totalBill+totalTime+totalDistance+totalLate+totalInventory),
                billedGridKwh=sessions.Where(s=>h.Population.Single(v=>v.Id==s.Observation.VehicleId).Kind==VehicleKind.Truck).Sum(s=>s.Observation.GridEnergyKwh) },
            vehicles=valued,sessions,recommendations,reconciliation,
            limitations=new[] { "No real invoice or VAT computation; NET is not tax-exempt.","No counterfactual outcome labels.",
                "Inventory correction is internal valuation, not sale of remaining energy.","Unresolved bills are unknown; totals are known accrued amounts, not a complete cost claim." } };
        EconomicJson.Write(Path.Combine(directory,"economic-report.json"),report);
        EconomicJson.Write(Path.Combine(directory,"reconciliation.json"),reconciliation);
        File.WriteAllLines(Path.Combine(directory,"cost-sessions.csv"),new[]{"sessionId,vehicleId,parkId,status,pricingStatus,gridKwh,actualKnownBillEur"}.Concat(
            sessions.Select(s=>string.Join(',',s.Observation.SessionId,s.Observation.VehicleId,s.Observation.ParkId,s.Status,s.PricingStatus,
                s.Observation.GridEnergyKwh.ToString(System.Globalization.CultureInfo.InvariantCulture),
                s.ActualAccruedBill?.Total.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)??"UNKNOWN"))));
    }

    public static void Replay(string sourcePath,string database,string output,string? fault=null,long faultSequence=0,string? marker=null)
    {
        if (fault is not (null or "before-commit" or "after-commit" or "before-batch-commit" or "after-batch-commit") || fault is not null && faultSequence<1)
            throw new ArgumentException("Invalid replay fault hook.");
        using var journal=new CostJournal(database);
        if (fault is not null)
            journal.FaultHook=(stage,sequence)=>
            {
                if (stage!=fault || sequence!=faultSequence) return;
                File.WriteAllText(marker??database+".fault",stage+":"+sequence);
                Thread.Sleep(Timeout.Infinite);
            };
        // Re-read committed prefixes as well: their hashes detect a source edited
        // in place. Checkpoints optimize recovery integrity, not permission to trust changes.
        using var source=new FileStream(sourcePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        using var buffer=new MemoryStream(); long offset=0; int next;
        var batch=new List<(CostSourceRecord Record,string Raw,long EndOffset)>();
        while ((next=source.ReadByte())!=-1)
        {
            offset++;
            if (next!=10) { buffer.WriteByte((byte)next); continue; }
            var line=Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r'); buffer.SetLength(0);
            var record=EconomicJson.Read<CostSourceRecord>(line);
            if (fault is "before-commit" or "after-commit") journal.Apply(record,line,offset);
            else
            {
                batch.Add((record,line,offset));
                if (batch.Count==256) { journal.ApplyBatch(batch); batch.Clear(); }
            }
        }
        if (batch.Count>0) journal.ApplyBatch(batch);
        if (journal.EndOffset>offset) throw new InvalidDataException("Source truncated behind committed watermark.");
        journal.Export(output);
    }
    public void Dispose() => db.Dispose();
}
