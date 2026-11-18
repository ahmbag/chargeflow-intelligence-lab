using System.Diagnostics;
using CFIL.Domain;

namespace CFIL.Simulation;

public sealed class SimulationEngine
{
    private readonly Scenario scenario;
    private readonly SimulationRun run;
    private readonly ITrafficSimulator traffic;
    private readonly ISimulationEventSink sink;
    private readonly IVehicleEnergyModel energy;
    private readonly IChargingPowerAllocator allocator;
    private readonly IChargingDecisionPolicy policy;
    private readonly ChargingPlanner? planner;
    private readonly IEconomicObservationSink? economics;
    private readonly List<TrafficCommand> commands = [];
    private readonly Dictionary<string, DecisionContext> pendingDecisions = new(StringComparer.Ordinal);
    private readonly List<ObservedOutcome> outcomes = [];
    private readonly Dictionary<string, ChargingSession> activeSessions = new(StringComparer.Ordinal);
    private readonly List<ChargingSession> sessions = [];
    private readonly List<double> stepMilliseconds = [];
    private readonly Stopwatch runtime = new();
    private long sequence;
    private bool finished;
    public long TimeMs { get; private set; }
    public IReadOnlyDictionary<string, Vehicle> Vehicles { get; }
    public IReadOnlyList<ChargingParkState> Parks { get; }
    public long EventCount => sequence;
    public long PeakWorkingSetBytes { get; private set; }
    public long PeakCombinedWorkingSetBytes { get; private set; }

    public SimulationEngine(Scenario scenario, SimulationRun run, ITrafficSimulator traffic, ISimulationEventSink sink,
        IVehicleEnergyModel? energy = null, IChargingPowerAllocator? allocator = null, IChargingDecisionPolicy? policy = null,
        ChargingPlanner? planner = null, IEconomicObservationSink? economics = null)
    {
        scenario.Validate();
        this.scenario = scenario; this.run = run; this.traffic = traffic; this.sink = sink;
        this.energy = energy ?? new VehicleEnergyModel(); this.allocator = allocator ?? new FairPowerAllocator();
        this.policy = policy ?? new NearestCompatiblePolicy();
        this.planner = planner;
        this.economics = economics;
        Vehicles = PopulationFactory.Create(scenario).ToDictionary(d => d.Id, d => new Vehicle(d), StringComparer.Ordinal);
        Parks = scenario.Parks.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => new ChargingParkState(p)).ToArray();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (sequence != 0) throw new InvalidOperationException("Already started.");
        runtime.Start();
        await Emit("SimulationStarted", run.RunId, new { scenario.Id, scenario.Seed, run.ScenarioHash }, cancellationToken);
        // Publish an explicit allowlist, never the scenario: future disturbances, departures and
        // routes are simulator knowledge, not information available to a queue-time observer.
        await Emit("ObservationContractPublished", run.RunId, new { contractVersion = "1.0",
            parks = Parks.Select(p => new { id = p.Definition.Id, p.AvailableGridKw,
                bays = p.Definition.Bays.Select(b => b.Id).ToArray(), blockedBays = p.BlockedBays.ToArray() }).ToArray() }, cancellationToken);
        await sink.SnapshotAsync(Snapshot(), cancellationToken);
    }

    public async Task StepAsync(CancellationToken cancellationToken)
    {
        if (finished || sequence == 0 || TimeMs >= scenario.DurationSeconds * 1000L) throw new InvalidOperationException("Cannot step this run.");
        var timer = Stopwatch.StartNew();
        // Guard BEFORE SUMO advances: keep enough energy for a full physical braking manoeuvre,
        // plus two reaction/step intervals. Instantaneous immobilization would cause rear-end collisions.
        foreach (var vehicle in Vehicles.Values.Where(v => v.Status is VehicleStatus.Driving or VehicleStatus.ApproachingPark or VehicleStatus.Departing or VehicleStatus.Connecting))
        {
            var d = vehicle.Definition;
            var brakingSeconds = Math.Ceiling(d.DesiredSpeedMps / 4.5) + 2;
            var brakingDistanceM = d.DesiredSpeedMps * d.DesiredSpeedMps / (2 * 4.5) + 2 * d.DesiredSpeedMps;
            var bound = d.Energy.ConsumptionAt(d.DesiredSpeedMps, d.GrossMassKg) * brakingDistanceM / 1000 + d.Energy.AuxiliaryKw * brakingSeconds / 3600;
            if (vehicle.Battery.EnergyKwh < bound)
                await Strand(vehicle, "energy-reserved-for-safe-stop", cancellationToken);
            else if (d.Logistics is not null && vehicle.DrivingSeconds >= d.Logistics.DrivingBudgetSeconds)
                await Strand(vehicle, "driving-time-budget-exhausted", cancellationToken);
        }
        var next = TimeMs + 1000;
        var result = await traffic.StepAsync(new(next, 1, commands.ToArray()), cancellationToken);
        commands.Clear();
        if (result.SimulationTimeMs != next || result.Teleports != 0 || result.Collisions != 0)
            throw new InvalidOperationException("Traffic clock mismatch, teleport or collision.");
        TimeMs = next;
        foreach (var park in Parks)
            if (park.UpdateCapacity(TimeMs))
                await Emit("ChargingParkCapacityChanged", park.Definition.Id,
                    new { park.AvailableGridKw, blockedBays = park.BlockedBays.Order().ToArray() }, cancellationToken);
        foreach (var id in result.Entered.Order(StringComparer.Ordinal))
        {
            var v = Vehicles[id]; v.Transition(VehicleStatus.Driving); v.EnteredMs = TimeMs;
            await EmitVehicle("VehicleEnteredHighway", v, new { kind = v.Definition.Kind.ToString(), soc = v.Battery.Soc }, cancellationToken);
            await EmitVehicle("ObservableVehicleRegistered", v, new { contractVersion = "1.0",
                kind = v.Definition.Kind.ToString(), v.Definition.CapacityKwh, v.Definition.Energy.MaxGridChargingKw,
                compatibleBays = Parks.ToDictionary(p => p.Definition.Id,
                    p => p.Definition.Bays.Where(b => p.Compatible(v.Definition, b)).Select(b => b.Id).ToArray()) }, cancellationToken);
        }
        if (result.Observations.Select(o => o.VehicleId).Distinct().Count() != result.Observations.Length)
            throw new InvalidOperationException("Duplicate traffic observations.");
        foreach (var observation in result.Observations.OrderBy(o => o.VehicleId, StringComparer.Ordinal))
        {
            var v = Vehicles[observation.VehicleId];
            if (v.Status == VehicleStatus.Stranded)
            {
                if (Math.Abs(observation.DistanceM - v.DistanceM) > 1e-5) throw new InvalidOperationException("Stranded vehicle moved.");
                continue;
            }
            var e = energy.Calculate(v, observation, TimeSpan.FromSeconds(1));
            if (!v.Battery.Consume(e.ConsumedKwh)) throw new InvalidOperationException("Energy guard underestimated next step.");
            v.ConsumedKwh += e.ConsumedKwh;
            v.DistanceM = observation.DistanceM; v.PositionKm = observation.PositionKm;
            v.EdgeId = observation.EdgeId; v.SpeedMps = observation.SpeedMps;
            if (v.SpeedMps > .1) v.DrivingSeconds++;
            else v.StationarySeconds++;
            if (v.SpeedMps <= .1 && v.Status is VehicleStatus.ApproachingPark or VehicleStatus.Connecting or VehicleStatus.Departing) v.AccessStoppedSeconds++;
            if (v.Status is VehicleStatus.Queued or VehicleStatus.Charging && v.SpeedMps <= .1) v.BreakSeconds++;
            if (v.Status == VehicleStatus.Stopping)
            {
                if (v.SpeedMps <= .001)
                {
                    v.Transition(VehicleStatus.Stranded);
                    await EmitVehicle("VehicleStranded", v, new { reason = v.StrandingReason, v.DistanceM, v.Battery.EnergyKwh }, cancellationToken);
                }
                continue;
            }
            if (observation.Arrived)
            {
                v.Transition(VehicleStatus.Arrived); v.ArrivedMs = TimeMs;
                await EmitVehicle("VehicleReachedDestination", v, new { soc = v.Battery.Soc, v.DistanceM,
                    reserveMet = v.Battery.EnergyKwh >= (v.Definition.Logistics?.RequiredReserveKwh ?? 0) }, cancellationToken);
                continue;
            }
            await ObserveParkMovement(v, observation, cancellationToken);
            // Decisions are sampled on the highway every 30 simulated seconds, never on an internal junction.
            if (v.Status == VehicleStatus.Driving && TimeMs % 30000 == 0 && v.EdgeId.StartsWith('H'))
                await Decide(v, cancellationToken);
            else if (planner is not null && v.Definition.Kind == VehicleKind.Truck && v.Status == VehicleStatus.ApproachingPark &&
                TimeMs % 30000 == 0 && v.EdgeId.StartsWith('H') && planner.ShouldReplan(v, Parks, TimeMs))
                await Decide(v, cancellationToken);
        }
        var expected = Vehicles.Values.Where(v => v.Status is not (VehicleStatus.Scheduled or VehicleStatus.Arrived)).Select(v => v.Id).ToHashSet();
        expected.ExceptWith(result.Observations.Select(o => o.VehicleId));
        if (expected.Count != 0) throw new InvalidOperationException("Missing active traffic observations.");
        foreach (var park in Parks)
        {
            await ProcessQueue(park, cancellationToken);
            await Charge(park, cancellationToken);
            park.BayOccupiedSeconds += park.BayOccupants.Count;
            park.AvailableGridKwh += park.AvailableGridKw / 3600;
        }
        Validate();
        if (economics is not null)
            await economics.ObserveAsync(TimeMs, sequence, Vehicles, sessions, Parks, false, "running", cancellationToken);
        if (planner is not null)
        {
            await planner.ReconcileAsync(Vehicles.Values, TimeMs, cancellationToken);
            if (TimeMs % 60000 == 0)
            {
                await sink.FlushAsync(cancellationToken);
                await planner.SynchronizeAsync(sequence, TimeMs, cancellationToken);
            }
        }
        if (TimeMs % (scenario.TelemetryIntervalSeconds * 1000L) == 0)
            await sink.TelemetryAsync(TimeMs, Vehicles.Values.Where(v => v.Status is not (VehicleStatus.Scheduled or VehicleStatus.Arrived))
                .Select(VehicleState).ToArray(), cancellationToken);
        if (TimeMs % (scenario.SnapshotIntervalSeconds * 1000L) == 0)
        {
            await sink.SnapshotAsync(Snapshot(), cancellationToken);
            await sink.FlushAsync(cancellationToken);
        }
        timer.Stop(); stepMilliseconds.Add(timer.Elapsed.TotalMilliseconds);
        if (TimeMs % 60000 == 0)
        {
            var working = Process.GetCurrentProcess().WorkingSet64;
            PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, working);
            PeakCombinedWorkingSetBytes = Math.Max(PeakCombinedWorkingSetBytes, working + result.ProviderWorkingSetBytes);
        }
    }

    private async Task Decide(Vehicle v, CancellationToken ct)
    {
        // Measure the entire held simulation barrier, including route/stop
        // verification and durable realized-decision delivery, not inference alone.
        var timer = Stopwatch.StartNew();
        try { await DecideCore(v, ct); }
        finally
        {
            if (v.Definition.Kind == VehicleKind.Truck) planner?.RecordDecisionDuration(timer.Elapsed.TotalSeconds);
        }
    }

    private async Task DecideCore(Vehicle v, CancellationToken ct)
    {
        var replanning = v.Status == VehicleStatus.ApproachingPark;
        var decision = replanning ? new ChargingDecision(v.ParkId, "observed-state-change", 0) : policy.Decide(v, Parks);
        PlanProposal? proposal = null;
        var controlled = planner is not null && planner.Options.Mode == PlanningMode.CONTROLLED && v.Definition.Kind == VehicleKind.Truck;
        if (planner is not null && v.Definition.Kind == VehicleKind.Truck && (decision.ParkId is not null || decision.Reason == "no-feasible-route"))
        {
            // The simulation cannot advance across this barrier. Python may only
            // consume the flushed event prefix, including earlier decisions at t.
            await sink.FlushAsync(ct);
            proposal = await planner.ProposeAsync(v, Parks, TimeMs, sequence, ct);
            if (controlled)
            {
                if (proposal.SelectedParkId is null)
                {
                    await Strand(v, "NO_FEASIBLE_CHARGING_OPTION", ct);
                    return;
                }
                if (replanning && proposal.SelectedParkId == v.ParkId) return;
                await planner.ApplyAsync(proposal, v, TimeMs, ct);
                if (replanning)
                {
                    // The old attempt never reached its queue. Preserve its
                    // non-observed outcome; only the replacement can earn a label.
                    await Outcome(v, "censored-abandoned", null, null, ct);
                    Parks.Single(p => p.Definition.Id == v.ParkId).Inbound.Remove(v.Id);
                }
                decision = new(proposal.SelectedParkId, "planning-" + planner.Options.Policy,
                    proposal.Candidates.Single(c => c.ParkId == proposal.SelectedParkId).Route.ApproachSeconds);
            }
        }
        if (decision.ParkId is null)
        {
            if (decision.Reason == "no-feasible-route") await Strand(v, decision.Reason, ct);
            return;
        }
        var park = Parks.Single(p => p.Definition.Id == decision.ParkId);
        var decisionId = $"{run.RunId}:decision:{sequence + 1:D9}";
        var context = new DecisionContext(decisionId, v.Id, v.Definition.Kind.ToString(), TimeMs, decision.ParkId, v.Battery.Soc,
            v.PositionKm, decision.EstimatedArrivalSeconds, park.BayOccupants.Count, park.Queue.Count, park.AvailableGridKw, decision.Reason);
        // Context records are immutable: the later outcome references the ID and never mutates these features.
        pendingDecisions.Add(decisionId, context); v.DecisionId = decisionId;
        await EmitVehicle("ChargingDecisionMade", v, context, ct);
        if (proposal is not null) await planner!.CommitAttemptAsync(proposal, decisionId, park.Definition.Id, ct);
        v.VisitedParks.Add(park.Definition.Id);
        if (!controlled && !park.HasQueueSpace)
        {
            await EmitVehicle("QueueAbandoned", v, new { decisionId, reason = "queue-overflow-before-diversion" }, ct);
            await Outcome(v, "rejected-overflow", null, null, ct);
            return;
        }
        // Controlled truck intents never reserve physical queue/bay capacity.
        // Legacy P0 and PKW retain their original admission behavior verbatim.
        if (!controlled) park.Inbound.Add(v.Id);
        v.ParkId = park.Definition.Id;
        if (!replanning)
        {
            v.Transition(VehicleStatus.ApproachingPark);
            await EmitVehicle("VehicleStateObserved", v, new { state = "ApproachingPark", v.ParkId }, ct);
        }
        if (!controlled) commands.Add(new("approach", v.Id, v.ParkId));
    }

    private async Task ObserveParkMovement(Vehicle v, TrafficObservation o, CancellationToken ct)
    {
        if (v.ParkId is null) return;
        var park = Parks.Single(p => p.Definition.Id == v.ParkId);
        if (v.Status == VehicleStatus.ApproachingPark && o.StoppedAt == v.ParkId + "-Q")
        {
            if (planner?.Options.Mode == PlanningMode.CONTROLLED && v.Definition.Kind == VehicleKind.Truck && !park.HasQueueSpace)
            {
                await EmitVehicle("QueueAbandoned", v, new { v.ParkId, reason = "physical-queue-capacity-at-arrival" }, ct);
                await Outcome(v, "rejected-overflow", null, null, ct);
                v.Transition(VehicleStatus.Departing);
                await EmitVehicle("VehicleStateObserved", v, new { state = "Departing", v.ParkId }, ct);
                commands.Add(new("release", v.Id));
                return;
            }
            await EmitVehicle("ChargingParkApproached", v, new { v.ParkId }, ct);
            park.Inbound.Remove(v.Id); v.QueueArrivalMs = TimeMs; v.BreakSeconds = 0;
            v.Transition(VehicleStatus.Queued);
            park.Queue.Add(new(v.Id, TimeMs, v.DecisionId!));
            park.MaxQueueLength = Math.Max(park.MaxQueueLength, park.Queue.Count);
            var compatible = park.Definition.Bays.Where(b => park.Compatible(v.Definition, b)).ToArray();
            await EmitVehicle("QueueEntered", v, new { v.ParkId, chargingAttemptId = v.DecisionId, soc = v.Battery.Soc,
                reasons = compatible.Select(b => park.UnavailableReason(v.Definition, b)).Where(r => r is not null).Distinct().ToArray() }, ct);
        }
        if (v.Status == VehicleStatus.Connecting && o.StoppedAt == v.ParkId + "-" + v.BayId)
        {
            if (v.ConnectedAtMs == 0) v.ConnectedAtMs = TimeMs;
            if (TimeMs - v.ConnectedAtMs >= scenario.ConnectingSeconds * 1000L)
            {
                v.Transition(VehicleStatus.Charging);
                var session = activeSessions[v.Id]; session.StartedMs = TimeMs;
                await EmitVehicle("ChargingStarted", v, new { session.Id, v.ParkId, v.BayId, chargingAttemptId = v.DecisionId, soc = v.Battery.Soc,
                    maneuveringSeconds = (v.ConnectedAtMs - session.BayReservedMs) / 1000d,
                    connectingSeconds = scenario.ConnectingSeconds }, ct);
                await Outcome(v, "observed", (TimeMs - v.QueueArrivalMs) / 1000d, session.Id, ct);
            }
        }
        if (v.Status == VehicleStatus.Departing && o.EdgeId.StartsWith('H'))
        {
            if (v.BayId is not null) park.BayOccupants.Remove(v.BayId);
            if (activeSessions.TryGetValue(v.Id, out var releasedSession)) releasedSession.ReleasedMs = TimeMs;
            activeSessions.Remove(v.Id);
            await EmitVehicle("VehicleDepartedPark", v, new { v.ParkId, v.BayId }, ct);
            v.Transition(VehicleStatus.Driving); v.ParkId = null; v.BayId = null; v.ConnectedAtMs = 0;
            if (v.Definition.Logistics is { } logistics && v.BreakSeconds >= logistics.RequiredBreakSeconds) v.DrivingSeconds = 0;
        }
    }

    private async Task ProcessQueue(ChargingParkState park, CancellationToken ct)
    {
        foreach (var entry in park.Queue.OrderBy(q => q.ArrivalMs).ThenBy(q => q.VehicleId, StringComparer.Ordinal).ToArray())
        {
            var v = Vehicles[entry.VehicleId];
            if (TimeMs - entry.ArrivalMs >= scenario.MaxQueueWaitSeconds * 1000L)
            {
                park.Queue.Remove(entry); v.Transition(VehicleStatus.Departing); commands.Add(new("release", v.Id));
                await EmitVehicle("QueueAbandoned", v, new { v.ParkId, reason = "maximum-wait-exceeded" }, ct);
                await EmitVehicle("VehicleStateObserved", v, new { state = "Departing", v.ParkId }, ct);
                await Outcome(v, "censored-abandoned", null, null, ct); continue;
            }
            var bay = park.FindFreeBay(v.Definition);
            if (bay is null) continue; // FIFO among eligible vehicles; an incompatible head must not block other types.
            park.Reserve(v.Definition, bay); park.Queue.Remove(entry);
            v.BayId = bay.Id; v.Transition(VehicleStatus.Connecting);
            var session = new ChargingSession($"{run.RunId}:session:{sessions.Count + 1:D6}", v.Id, park.Definition.Id,
                bay.Id, entry.DecisionId, entry.ArrivalMs, TimeMs);
            sessions.Add(session); activeSessions.Add(v.Id, session);
            commands.Add(new("bay", v.Id, v.ParkId, bay.Id));
            await EmitVehicle("VehicleStateObserved", v, new { state = "Connecting", v.ParkId, v.BayId }, ct);
        }
    }

    private async Task Charge(ChargingParkState park, CancellationToken ct)
    {
        var charging = activeSessions.Values.Where(s => s.ParkId == park.Definition.Id && Vehicles[s.VehicleId].Status == VehicleStatus.Charging)
            .OrderBy(s => s.VehicleId, StringComparer.Ordinal).ToArray();
        var requests = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var s in charging)
        {
            var v = Vehicles[s.VehicleId]; var bay = park.Definition.Bays.Single(b => b.Id == s.BayId);
            // The remaining target energy is a power cap for this exact one-second interval.
            // This avoids claiming grid energy that the nearly-full battery cannot accept.
            var targetCap = Math.Max(0, v.Definition.Energy.TargetSoc * v.Battery.CapacityKwh - v.Battery.EnergyKwh) * 3600 / v.Definition.Energy.Efficiency;
            requests[v.Id] = park.BlockedBays.Contains(bay.Id) || s.StartedMs == TimeMs ? 0 :
                Math.Min(targetCap, Math.Min(bay.Connector.MaxGridPowerKw, v.Definition.Energy.AcceptanceKw(v.Battery.Soc)));
        }
        var allocation = allocator.Allocate(park.AvailableGridKw, requests);
        foreach (var s in charging)
        {
            var v = Vehicles[s.VehicleId]; var kw = allocation[v.Id];
            if (Math.Abs(s.AssignedGridKw - kw) > .01)
                await EmitVehicle("ChargingPowerChanged", v, new { s.Id, assignedPowerKw = kw }, ct);
            s.AssignedGridKw = kw;
            var added = v.Battery.Charge(kw, v.Definition.Energy.Efficiency, 1, v.Definition.Energy.TargetSoc);
            var grid = added / v.Definition.Energy.Efficiency;
            v.ChargedKwh += added; s.BatteryEnergyKwh += added; s.GridEnergyKwh += grid; park.GridEnergyKwh += grid;
            if (kw > 0) park.ConnectorActiveSeconds++;
            if (v.Battery.Soc + 1e-10 < v.Definition.Energy.TargetSoc) continue;
            if (s.EndedMs is null)
            {
                s.EndedMs = TimeMs; s.Outcome = "completed"; s.AssignedGridKw = 0;
                await EmitVehicle("ChargingCompleted", v, new { s.Id, s.BatteryEnergyKwh, s.GridEnergyKwh, soc = v.Battery.Soc }, ct);
            }
            if (!park.CanExit(s.BayId))
            {
                v.ExitBlockedSinceMs ??= TimeMs;
                if (TimeMs - v.ExitBlockedSinceMs >= scenario.DeadlockTimeoutSeconds * 1000L)
                    throw new InvalidOperationException($"Park deadlock: {v.ParkId}/{v.BayId}; occupancy retained.");
                continue;
            }
            v.ExitBlockedSinceMs = null;
            v.Transition(VehicleStatus.Departing); commands.Add(new("release", v.Id));
            await EmitVehicle("VehicleStateObserved", v, new { state = "Departing", v.ParkId, v.BayId }, ct);
        }
    }

    private async Task Strand(Vehicle v, string reason, CancellationToken ct)
    {
        // Cancel any pending diversion/admission before immobilizing. Physical occupancy is retained,
        // because releasing a bay here would make a real stranded obstruction disappear.
        commands.RemoveAll(c => c.VehicleId == v.Id);
        v.StrandingReason = reason;
        v.Transition(v.SpeedMps <= .001 ? VehicleStatus.Stranded : VehicleStatus.Stopping); commands.Add(new("strand", v.Id));
        if (v.Status == VehicleStatus.Stranded)
            await EmitVehicle("VehicleStranded", v, new { reason, v.DistanceM, v.Battery.EnergyKwh }, ct);
        else
            await EmitVehicle("VehicleStateObserved", v, new { state = "Stopping", reason, v.DistanceM, v.Battery.EnergyKwh }, ct);
        if (v.DecisionId is not null && pendingDecisions.ContainsKey(v.DecisionId)) await Outcome(v, "censored-stranded", null, null, ct);
        foreach (var p in Parks) { p.Inbound.Remove(v.Id); p.Queue.RemoveAll(q => q.VehicleId == v.Id); }
        if (activeSessions.TryGetValue(v.Id, out var session)) { session.Outcome = "aborted-stranded"; session.AssignedGridKw = 0; }
    }

    private async Task Outcome(Vehicle v, string status, double? wait, string? session, CancellationToken ct)
    {
        var id = v.DecisionId ?? throw new InvalidOperationException("Missing decision context.");
        if (!pendingDecisions.Remove(id)) throw new InvalidOperationException("Duplicate outcome.");
        var outcome = new ObservedOutcome(id, TimeMs, status, wait, session); outcomes.Add(outcome);
        await EmitVehicle("ChargingOutcomeObserved", v, outcome, ct);
        if (planner is not null) await planner.OutcomeAsync(outcome, ct);
    }

    public void Validate()
    {
        foreach (var v in Vehicles.Values)
        {
            if (!double.IsFinite(v.Battery.EnergyKwh) || v.Battery.EnergyKwh < 0 || v.Battery.EnergyKwh > v.Battery.CapacityKwh)
                throw new InvalidOperationException("Battery bounds violated.");
            var balance = v.Definition.InitialSoc * v.Battery.CapacityKwh + v.ChargedKwh - v.ConsumedKwh;
            if (Math.Abs(balance - v.Battery.EnergyKwh) > 1e-6) throw new InvalidOperationException("Energy conservation violated.");
        }
        var allOccupants = new HashSet<string>();
        foreach (var p in Parks)
        {
            if (p.Queue.Count + p.Inbound.Count > p.Definition.QueueCapacity) throw new InvalidOperationException("Queue overflow.");
            foreach (var (bayId, vehicleId) in p.BayOccupants)
            {
                var v = Vehicles[vehicleId]; var bay = p.Definition.Bays.Single(b => b.Id == bayId);
                if (!allOccupants.Add(vehicleId) || !p.Compatible(v.Definition, bay) || v.BayId != bayId || v.ParkId != p.Definition.Id)
                    throw new InvalidOperationException("Invalid bay occupancy.");
                if (bay.Conflicts.Any(p.BayOccupants.ContainsKey)) throw new InvalidOperationException("Geometric conflict.");
            }
            var allocated = activeSessions.Values.Where(s => s.ParkId == p.Definition.Id).Sum(s => s.AssignedGridKw);
            if (allocated > p.AvailableGridKw + 1e-6) throw new InvalidOperationException("Grid overload.");
            foreach (var s in activeSessions.Values.Where(s => s.ParkId == p.Definition.Id))
            {
                var v = Vehicles[s.VehicleId]; var bay = p.Definition.Bays.Single(b => b.Id == s.BayId);
                if (s.AssignedGridKw < 0 || s.AssignedGridKw > Math.Min(bay.Connector.MaxGridPowerKw, v.Definition.Energy.MaxGridChargingKw) + 1e-6)
                    throw new InvalidOperationException("Connector or vehicle power exceeded.");
            }
        }
    }

    public async Task<RunResult> FinishAsync(string status, string? error, CancellationToken ct)
    {
        if (finished) throw new InvalidOperationException("Already finished.");
        finished = true; runtime.Stop();
        foreach (var decision in pendingDecisions.Values.OrderBy(d => d.DecisionId, StringComparer.Ordinal).ToArray())
        {
            var outcome = new ObservedOutcome(decision.DecisionId, TimeMs, status == "completed" ? "censored-end-of-run" : "censored-run-failure", null, null);
            outcomes.Add(outcome);
            await Emit("ChargingOutcomeObserved", decision.VehicleId, outcome, ct);
            if (planner is not null) await planner.OutcomeAsync(outcome, ct);
        }
        pendingDecisions.Clear();
        await Emit(status == "completed" ? "SimulationCompleted" : "SimulationFailed", run.RunId, new { status, error }, ct);
        var snapshot = Snapshot(); await sink.SnapshotAsync(snapshot, ct); await sink.FlushAsync(ct);
        if (economics is not null)
            await economics.ObserveAsync(TimeMs, sequence, Vehicles, sessions, Parks, true, status, ct);
        return new(TimeMs, sequence, Metrics(status), snapshot);
    }

    public SimulationSnapshot Snapshot() => new("1.0", run.RunId, TimeMs, sequence,
        Vehicles.Values.Select(VehicleState).ToArray(), Parks.Select(p => new ParkSnapshot(p.Definition.Id, p.AvailableGridKw,
            new(p.BayOccupants, StringComparer.Ordinal), p.Queue.Select(q => q.VehicleId).ToArray(), p.Inbound.Order().ToArray())).ToArray());
    private static VehicleSnapshot VehicleState(Vehicle v) => new(v.Id, v.Status.ToString(), v.Battery.CapacityKwh, v.Battery.EnergyKwh,
        v.DistanceM, v.PositionKm, v.EdgeId, v.ParkId, v.BayId, v.ChargedKwh, v.ConsumedKwh);

    private object Metrics(string status)
    {
        static object Summary(IEnumerable<double> source)
        {
            var values = source.Order().ToArray();
            double? Q(double q) => values.Length == 0 ? null : values[(int)Math.Ceiling(q * values.Length) - 1];
            return new { count = values.Length, mean = values.Length == 0 ? (double?)null : values.Average(), median = Q(.5), p50 = Q(.5), p95 = Q(.95) };
        }
        var vehicles = Vehicles.Values.ToArray();
        return new
        {
            schemaVersion = "1.0", run.RunId, status, simulationSeconds = TimeMs / 1000, eventCount = sequence,
            population = new { trucks = vehicles.Count(v => v.Definition.Kind == VehicleKind.Truck), cars = vehicles.Count(v => v.Definition.Kind == VehicleKind.Car) },
            byKind = Enum.GetValues<VehicleKind>().Select(kind => new
            {
                kind = kind.ToString(), total = vehicles.Count(v => v.Definition.Kind == kind),
                entered = vehicles.Count(v => v.Definition.Kind == kind && v.EnteredMs is not null),
                arrived = vehicles.Count(v => v.Definition.Kind == kind && v.Status == VehicleStatus.Arrived),
                stranded = vehicles.Count(v => v.Definition.Kind == kind && v.Status == VehicleStatus.Stranded),
                incomplete = vehicles.Count(v => v.Definition.Kind == kind && v.Status is not (VehicleStatus.Arrived or VehicleStatus.Stranded)),
                waitSeconds = Summary(outcomes.Where(o => o.WaitingSeconds is not null && Vehicles[sessions.Single(s => s.Id == o.SessionId).VehicleId].Definition.Kind == kind).Select(o => o.WaitingSeconds!.Value)),
                travelSeconds = Summary(vehicles.Where(v => v.Definition.Kind == kind && v.ArrivedMs is not null).Select(v => (v.ArrivedMs!.Value - v.EnteredMs!.Value) / 1000d)),
                delayedDepartureSeconds = Summary(vehicles.Where(v => v.Definition.Kind == kind && v.EnteredMs is not null).Select(v => v.EnteredMs!.Value / 1000d - v.Definition.DepartureSeconds)),
                stationarySeconds = vehicles.Where(v => v.Definition.Kind == kind).Sum(v => v.StationarySeconds),
                accessStoppedSeconds = vehicles.Where(v => v.Definition.Kind == kind).Sum(v => v.AccessStoppedSeconds),
                blockedVehiclesAtEnd = vehicles.Count(v => v.Definition.Kind == kind && v.SpeedMps <= .1 && v.Status is VehicleStatus.ApproachingPark or VehicleStatus.Connecting or VehicleStatus.Departing)
            }).ToArray(),
            charging = new { sessions = sessions.Count, completed = sessions.Count(s => s.Outcome == "completed"),
                incomplete = sessions.Count(s => s.Outcome == "pending"), aborted = sessions.Count(s => s.Outcome.StartsWith("aborted", StringComparison.Ordinal)),
                batteryEnergyKwh = sessions.Sum(s => s.BatteryEnergyKwh), gridEnergyKwh = sessions.Sum(s => s.GridEnergyKwh),
                durationSeconds = Summary(sessions.Where(s => s.EndedMs is not null).Select(s => (s.EndedMs!.Value - s.StartedMs!.Value) / 1000d)),
                averageGridPowerKw = Summary(sessions.Where(s => s.EndedMs > s.StartedMs).Select(s => s.GridEnergyKwh * 3600000 / (s.EndedMs!.Value - s.StartedMs!.Value))),
                outcomes = outcomes.GroupBy(o => o.Status).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()) },
            logistics = new { successfulDeliveries = vehicles.Count(v => v.Definition.Kind == VehicleKind.Truck && v.Status == VehicleStatus.Arrived),
                reserveFailures = vehicles.Count(v => v.Status == VehicleStatus.Arrived && v.Definition.Logistics is { } l && v.Battery.EnergyKwh < l.RequiredReserveKwh),
                latenessSeconds = Summary(vehicles.Where(v => v.ArrivedMs is not null && v.Definition.Logistics is not null)
                    .Select(v => Math.Max(0, v.ArrivedMs!.Value - v.Definition.Logistics!.TargetArrivalMs) / 1000d)) },
            parks = Parks.Select(p => new { id = p.Definition.Id, p.GridEnergyKwh, p.MaxQueueLength,
                bayUtilization = TimeMs == 0 ? 0 : p.BayOccupiedSeconds / (p.Definition.Bays.Length * TimeMs / 1000d),
                connectorUtilization = TimeMs == 0 ? 0 : p.ConnectorActiveSeconds / (p.Definition.Bays.Length * TimeMs / 1000d),
                gridUtilization = p.AvailableGridKwh == 0 ? 0 : p.GridEnergyKwh / p.AvailableGridKwh,
                finalQueue = p.Queue.Count, finalInbound = p.Inbound.Count, finalOccupied = p.BayOccupants.Count }).ToArray(),
            integrity = new { invariantsPassed = status == "completed", invalidStates = status == "completed" ? 0 : 1 },
            performance = new { wallSeconds = runtime.Elapsed.TotalSeconds, simulationSpeed = runtime.Elapsed.TotalSeconds == 0 ? 0 : TimeMs / 1000d / runtime.Elapsed.TotalSeconds,
                stepMilliseconds = Summary(stepMilliseconds), peakDotnetWorkingSetBytes = PeakWorkingSetBytes,
                peakCombinedWorkingSetBytes = PeakCombinedWorkingSetBytes, memorySamplingIntervalSeconds = 60 }
        };
    }

    private async Task EmitVehicle(string type, Vehicle vehicle, object payload, CancellationToken ct)
    {
        var e = await Emit(type, vehicle.Id, payload, ct, vehicle.LastEventId); vehicle.LastEventId = e;
    }
    private async Task<string> Emit(string type, string entity, object payload, CancellationToken ct, string? cause = null)
    {
        var next = ++sequence;
        var id = $"{run.RunId}:{next:D9}";
        await sink.AppendAsync(new("1.0", run.RunId, next, id, TimeMs, type, entity, cause, payload), ct);
        return id;
    }
}
