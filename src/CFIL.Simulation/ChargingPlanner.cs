using System.Diagnostics;
using CFIL.Domain;

namespace CFIL.Simulation;

public static class ChargingPlanningRules
{
    public const string FeatureVersion = "PRE_ARRIVAL_DECISION_V1";

    public static double ChargingSeconds(VehicleEnergyProfile profile, double capacityKwh, double arrivalSoc,
        double targetSoc, double gridKw)
    {
        if (!double.IsFinite(gridKw) || gridKw <= 0 || !double.IsFinite(arrivalSoc) || arrivalSoc < 0 ||
            arrivalSoc > 1 || !double.IsFinite(targetSoc) || targetSoc < 0 || targetSoc > 1 ||
            !double.IsFinite(capacityKwh) || capacityKwh <= 0) throw new ArgumentException("Invalid charging estimate.");
        double seconds = 0;
        // Integrate the existing SoC-dependent acceptance curve. Grid limits and
        // efficiency apply to every slice; nameplate power alone is not a charge time.
        for (var soc = arrivalSoc; soc < targetSoc;)
        {
            var next = Math.Min(targetSoc, soc + .002);
            var power = Math.Min(gridKw, profile.AcceptanceKw((soc + next) / 2));
            seconds += capacityKwh * (next - soc) * 3600 / (power * profile.Efficiency);
            soc = next;
        }
        return seconds;
    }

    public static ChargingCandidate Candidate(ChargingDecisionContext context, VehicleDefinition vehicle,
        PlanningPark park, RouteCandidate route, PlanningOptions options)
    {
        var reasons = new List<string>();
        if (!route.Legal) reasons.Add(route.Rejection ?? "NO_LEGAL_ROUTE");
        if (route.Legal && (route.RouteEdges.Length == 0 || route.RouteEdges[0] != route.CurrentEdge ||
            route.RouteEdges.Distinct().Count() != route.RouteEdges.Length ||
            new[] { route.ApproachDistanceM, route.ApproachSeconds, route.ContinuationDistanceM,
                route.ContinuationSeconds, route.CurrentLanePositionM }.Any(n => !double.IsFinite(n) || n < 0)))
            throw new InvalidDataException("Malformed route evidence; candidate calculation is unsafe.");
        if (route.QueriedAtMs != context.TimeMs || route.CurrentEdge != context.Vehicle.EdgeId) reasons.Add("STALE_ROUTE");
        if (park.PositionKm <= context.Vehicle.PositionKm || park.PositionKm >= vehicle.RouteEndKm) reasons.Add("WRONG_DIRECTION_OR_DESTINATION");
        if (park.CompatibleBays == 0) reasons.Add("CONNECTOR_GEOMETRY_OR_ACCESS");
        if (park.GridKw <= 0 || park.BlockedCompatibleBays >= park.CompatibleBays) reasons.Add("KNOWN_UNAVAILABLE");
        var consumption = vehicle.Energy.ConsumptionAt(vehicle.DesiredSpeedMps, vehicle.GrossMassKg);
        var toQueue = route.ApproachDistanceM / 1000 * consumption + vehicle.Energy.AuxiliaryKw * route.ApproachSeconds / 3600;
        var auxiliaryReserve = vehicle.Energy.AuxiliaryKw * options.AuxiliaryReserveSeconds / 3600;
        var reserve = vehicle.Logistics?.RequiredReserveKwh ?? vehicle.CapacityKwh * .05;
        if (context.Vehicle.EnergyKwh + 1e-9 < toQueue + auxiliaryReserve + reserve) reasons.Add("INSUFFICIENT_ENERGY_WITH_RESERVES");
        var arrival = Math.Clamp((context.Vehicle.EnergyKwh - toQueue) / vehicle.CapacityKwh, 0, 1);
        var target = vehicle.Energy.TargetSoc;
        var nextOpportunity = context.Parks.Where(p => p.PositionKm > park.PositionKm && p.CompatibleBays > p.BlockedCompatibleBays && p.GridKw > 0)
            .Select(p => p.PositionKm).Append(vehicle.RouteEndKm).Min();
        var continuationKm = Math.Min(route.ContinuationDistanceM / 1000, nextOpportunity - park.PositionKm + 2);
        var continuationEnergy = continuationKm * (consumption + vehicle.Energy.AuxiliaryKw / (vehicle.DesiredSpeedMps * 3.6));
        if (vehicle.CapacityKwh * Math.Max(arrival, target) < continuationEnergy + reserve + auxiliaryReserve)
            reasons.Add("NO_PLAUSIBLE_CONTINUATION");
        var assumedPower = Math.Min(park.ConnectorKw, park.GridKw / Math.Max(1, park.Occupied + 1));
        var duration = assumedPower > 0 ? ChargingSeconds(vehicle.Energy, vehicle.CapacityKwh, arrival, target, assumedPower) : 0;
        var intents = context.Intents.Where(i => i.ParkId == park.Id && i.VehicleId != vehicle.Id &&
            i.ExpiresMs > context.TimeMs && i.EstimatedArrivalMs <= context.TimeMs + route.ApproachSeconds * 1000).ToArray();
        var deterministic = park.Queue * duration / Math.Max(1, park.CompatibleBays - park.BlockedCompatibleBays);
        var features = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["soc"] = context.Vehicle.Soc, ["capacityKwh"] = vehicle.CapacityKwh,
            ["maxChargingKw"] = vehicle.Energy.MaxGridChargingKw, ["estimatedTravelSeconds"] = route.ApproachSeconds,
            ["estimatedEnergyKwh"] = toQueue, ["estimatedArrivalSoc"] = arrival,
            ["queueNow"] = park.Queue, ["occupiedCompatibleNow"] = park.Occupied,
            ["compatibleBays"] = park.CompatibleBays, ["blockedCompatibleNow"] = park.BlockedCompatibleBays,
            ["availableGridKwNow"] = park.GridKw, ["knownIntentCount"] = intents.Length,
            ["knownIntentEnergyKwh"] = intents.Sum(i => i.EnergyKwh), ["timeOfDaySeconds"] = context.TimeMs / 1000d % 86400
        };
        return new(park.Id, route, reasons.Count == 0, reasons.ToArray(), toQueue, auxiliaryReserve, reserve, arrival,
            target, Math.Max(0, (target - arrival) * vehicle.CapacityKwh), assumedPower, duration, continuationEnergy,
            deterministic, intents.Length * options.IntentPenaltySeconds, features);
    }

    public static CandidateScore[] Score(ChargingDecisionContext context, ChargingCandidate[] candidates,
        ForecastBatch batch, PlanningOptions options)
    {
        var validBatch = ValidForecast(context, candidates, batch);
        var scores = new List<CandidateScore>();
        foreach (var candidate in candidates.Where(c => c.Feasible))
        {
            var forecast = validBatch ? batch.Forecasts.SingleOrDefault(f => f.ParkId == candidate.ParkId) : null;
            var ml = context.Policy is PlanningPolicy.P2 or PlanningPolicy.P3 && forecast is { Supported: true, Method: "river" };
            var wait = ml ? forecast!.WaitSeconds : forecast?.BaselineSeconds ?? candidate.DeterministicWaitSeconds;
            var travel = candidate.Route.ApproachSeconds + candidate.Route.ContinuationSeconds;
            var late = Math.Max(0, (context.TimeMs - context.Vehicle.TargetArrivalMs) / 1000d + travel + wait + candidate.ChargeSeconds) * options.LatenessWeight;
            var change = context.PreviousParkId is not null && context.PreviousParkId != candidate.ParkId ? options.SwitchPenaltySeconds : 0;
            scores.Add(new(candidate.ParkId, travel, wait, candidate.ChargeSeconds, late, change, candidate.CoordinationSeconds,
                travel + wait + candidate.ChargeSeconds + late + change + candidate.CoordinationSeconds,
                ml ? "river" : validBatch ? "historical-or-structural" : "invalid-provider-fallback"));
        }
        return scores.OrderBy(s => s.TotalSeconds).ThenBy(s => s.ParkId, StringComparer.Ordinal).ToArray();
    }

    public static bool ValidForecast(ChargingDecisionContext context, ChargingCandidate[] candidates, ForecastBatch batch) =>
            batch.DecisionId == context.DecisionId && batch.SourceSequence == context.SourceSequence &&
            batch.Forecasts.Length == candidates.Length && batch.Forecasts.Select(f => f.ParkId).Distinct().Count() == candidates.Length &&
            batch.Forecasts.All(f => candidates.Any(c => c.ParkId == f.ParkId)) &&
            batch.Forecasts.All(f => f.Revision == batch.Revision && f.SourceSequence == context.SourceSequence &&
                (f.ModelVersion == "PREARRIVAL_RIVER_V1" || f.ModelVersion == "unavailable" && f.Method == "structural" && !f.Supported && f.Revision == -1) &&
                f.FeatureVersion == FeatureVersion && double.IsFinite(f.WaitSeconds) && f.WaitSeconds >= 0 &&
                double.IsFinite(f.BaselineSeconds) && f.BaselineSeconds >= 0);
    public static string? Select(ChargingDecisionContext context, CandidateScore[] scores, PlanningOptions options)
    {
        if (scores.Length == 0) return null;
        if (context.PreviousParkId is null) return scores[0].ParkId;
        var current = scores.SingleOrDefault(s => s.ParkId == context.PreviousParkId);
        if (context.Changes >= options.MaxPlanChanges) return current?.ParkId;
        if (current is not null && current.TotalSeconds - scores[0].TotalSeconds < options.ReplanImprovementSeconds) return current.ParkId;
        return scores[0].ParkId;
    }
}

public sealed class ChargingPlanner(PlanningOptions options, SimulationRun run, IPlanningTraffic traffic,
    IWaitForecastProvider forecasts, IDecisionTrace trace, IEconomicPlanningLayer? economics = null) : IAsyncDisposable
{
    private readonly Dictionary<string, ArrivalIntent> intents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> revisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> changes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Time, int Queue, double Grid)> lastPlans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> attempts = new(StringComparer.Ordinal);
    private long decisions;
    public PlanningOptions Options => options;
    public double MaxCandidateSeconds { get; private set; }
    public double MaxForecastSeconds { get; private set; }
    public double MaxPlanningSeconds { get; private set; }
    public double MaxDecisionBarrierSeconds { get; private set; }
    public void RecordDecisionDuration(double seconds) => MaxDecisionBarrierSeconds = Math.Max(MaxDecisionBarrierSeconds, seconds);
    public int Applied { get; private set; }
    public int MlApplied { get; private set; }
    public int Fallbacks { get; private set; }
    public int Replans { get; private set; }

    public bool ShouldReplan(Vehicle vehicle, IReadOnlyList<ChargingParkState> parks, long now)
    {
        if (options.Mode != PlanningMode.CONTROLLED || vehicle.ParkId is null || !lastPlans.TryGetValue(vehicle.Id, out var last)) return false;
        var park = parks.Single(p => p.Definition.Id == vehicle.ParkId);
        var closed = park.AvailableGridKw <= 0 || !park.Definition.Bays.Any(b => park.Compatible(vehicle.Definition, b) && !park.BlockedBays.Contains(b.Id));
        return closed || now - last.Time >= options.ReplanCooldownSeconds * 1000L &&
            (Math.Abs(park.Queue.Count - last.Queue) >= 3 || Math.Abs(park.AvailableGridKw - last.Grid) > .01);
    }

    public async Task<PlanProposal> ProposeAsync(Vehicle vehicle, IReadOnlyList<ChargingParkState> parks, long now, long sequence, CancellationToken ct)
    {
        var total = Stopwatch.StartNew(); var timer = Stopwatch.StartNew();
        var definition = vehicle.Definition;
        var parkViews = parks.Select(p =>
        {
            var compatible = p.Definition.Bays.Where(b => p.Compatible(definition, b)).ToArray();
            return new PlanningPark(p.Definition.Id, p.Definition.PositionKm, p.AvailableGridKw, p.Queue.Count,
                compatible.Count(b => p.BayOccupants.ContainsKey(b.Id)), compatible.Length,
                compatible.Count(b => p.BlockedBays.Contains(b.Id) || b.ExitBlockedBy.Any(p.BlockedBays.Contains)),
                compatible.Select(b => b.Connector.MaxGridPowerKw).DefaultIfEmpty(0).Max(), compatible.Select(b => b.Id).ToArray());
        }).ToArray();
        var revision = revisions.GetValueOrDefault(vehicle.Id);
        var id = $"{run.RunId}:planning:{++decisions:D6}";
        var context = new ChargingDecisionContext("1.0", run.RunId, id, vehicle.Id, vehicle.Id + ":trip-1", id + ":plan",
            revision, revision + 1, now, sequence, now + options.ValidForSeconds * 1000L, options.Mode, options.Policy,
            vehicle.ParkId is null ? "EXISTING_CHARGING_NEED" : "OBSERVED_STATE_CHANGE",
            new(vehicle.Id, vehicle.Id + ":trip-1", vehicle.EdgeId, vehicle.PositionKm, vehicle.Battery.EnergyKwh,
                vehicle.Battery.Soc, definition.CapacityKwh, definition.Energy.MaxGridChargingKw, definition.DesiredSpeedMps,
                definition.GrossMassKg, definition.Energy.AuxiliaryKw, definition.Logistics!.RequiredReserveKwh,
                definition.RouteEndKm, definition.Logistics.TargetArrivalMs, vehicle.Status.ToString()),
            parkViews, intents.Values.Where(i => i.ExpiresMs > now).OrderBy(i => i.VehicleId, StringComparer.Ordinal).ToArray(),
            vehicle.ParkId, changes.GetValueOrDefault(vehicle.Id));
        var routes = await traffic.FindChargingRoutesAsync(vehicle.Id, parks.Select(p => p.Definition.Id).ToArray(), ct);
        var candidates = parkViews.Select(p => ChargingPlanningRules.Candidate(context, definition, p,
            routes.Single(r => r.ParkId == p.Id), options)).Select(c => vehicle.VisitedParks.Contains(c.ParkId) && c.ParkId != vehicle.ParkId
                ? c with { Feasible = false, Rejections = [.. c.Rejections, "ALREADY_VISITED"] } : c).ToArray();
        MaxCandidateSeconds = Math.Max(MaxCandidateSeconds, timer.Elapsed.TotalSeconds); timer.Restart();
        ForecastBatch batch;
        try
        {
            batch = await forecasts.ForecastAsync(context, candidates, ct);
            if (!ChargingPlanningRules.ValidForecast(context, candidates, batch))
                throw new IOException("Forecast batch violates checkpoint/feature/information contract.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            Fallbacks++;
            batch = new(context.DecisionId, -1, sequence, "unavailable", candidates.Select(c => new WaitForecast(c.ParkId,
                c.DeterministicWaitSeconds, c.DeterministicWaitSeconds, null, "structural", ex.GetType().Name,
                ChargingPlanningRules.FeatureVersion, "unavailable", -1, sequence, false)).ToArray());
            await trace.AppendAsync("ForecastUnavailable", new { context.DecisionId, error = ex.Message }, ct);
        }
        MaxForecastSeconds = Math.Max(MaxForecastSeconds, timer.Elapsed.TotalSeconds);
        var scores = ChargingPlanningRules.Score(context, candidates, batch, options);
        var selected = ChargingPlanningRules.Select(context, scores, options);
        if (vehicle.ParkId is { } oldPark && (parkViews.Single(p => p.Id == oldPark).PositionKm - vehicle.PositionKm) * 1000 <= options.CommitmentDistanceM)
            selected = scores.Any(s => s.ParkId == oldPark) ? oldPark : null;
        var proposal = new PlanProposal(context, candidates, batch, scores, selected,
            selected is null ? "NO_FEASIBLE_CHARGING_OPTION" : selected == vehicle.ParkId ? "KEEP_COMMITTED_PLAN" : "MINIMUM_FEASIBLE_SCORE");
        if (economics is not null) proposal = await economics.EvaluateAsync(proposal, vehicle, options, ct);
        await trace.AppendAsync("Decision", proposal, ct);
        MaxPlanningSeconds = Math.Max(MaxPlanningSeconds, total.Elapsed.TotalSeconds);
        return proposal;
    }

    public async Task ApplyAsync(PlanProposal proposal, Vehicle vehicle, long now, CancellationToken ct)
    {
        var context = proposal.Context;
        var candidate = proposal.Candidates.Single(c => c.ParkId == proposal.SelectedParkId);
        if (options.Mode != PlanningMode.CONTROLLED || !candidate.Feasible || now > context.ValidUntilMs ||
            vehicle.EdgeId != context.Vehicle.EdgeId || Math.Abs(vehicle.PositionKm - context.Vehicle.PositionKm) > .000001 ||
            vehicle.Battery.EnergyKwh + 1e-9 < candidate.EnergyToQueueKwh + candidate.AuxiliaryReserveKwh + candidate.MinimumReserveKwh ||
            revisions.GetValueOrDefault(vehicle.Id) != context.ExpectedRevision ||
            vehicle.Status is not (VehicleStatus.Driving or VehicleStatus.ApproachingPark))
            throw new InvalidOperationException("Plan guard rejected stale, unsafe or non-controllable decision.");
        var application = new PlanApplication(context.PlanId, vehicle.Id, context.ExpectedRevision, context.Revision,
            context.ValidUntilMs, candidate.Route, options.Fault == "after-route-once" && Applied == 0 ? "after-route" : null);
        // Persist the intent BEFORE external mutation. A TraCI command is not part
        // of our SQLite transaction; an ambiguous/partial result must stop the run.
        await trace.AppendAsync("APPLYING", application, ct);
        var receipt = await traffic.ApplyPlanAsync(application, ct);
        await trace.AppendAsync(receipt.Status, receipt, ct);
        if (receipt.Status != "APPLIED" || receipt.PlanId != context.PlanId || receipt.Revision != context.Revision ||
            receipt.RouteIndex < 0 || receipt.RouteIndex >= receipt.ActualRoute.Length ||
            receipt.ActualRoute[receipt.RouteIndex] != receipt.CurrentEdge ||
            !receipt.ActualRoute.Skip(receipt.RouteIndex).SequenceEqual(candidate.Route.RouteEdges) ||
            !receipt.ActualStops.SequenceEqual([candidate.ParkId + "-Q"]))
            throw new IOException("Plan application failed or could not be reconciled with actual SUMO route/stops.");
        revisions[vehicle.Id] = context.Revision;
        if (vehicle.ParkId is not null) { changes[vehicle.Id] = changes.GetValueOrDefault(vehicle.Id) + 1; Replans++; }
        Applied++;
        if (proposal.Scores.Single(s => s.ParkId == candidate.ParkId).Method == "river") MlApplied++;
        else if (options.Policy is PlanningPolicy.P2 or PlanningPolicy.P3) Fallbacks++;
    }

    public async Task CommitAttemptAsync(PlanProposal proposal, string attemptId, string actualPark, CancellationToken ct)
    {
        if (economics is not null) await economics.RealizedAsync(proposal, attemptId, actualPark, ct);
        var candidate = proposal.Candidates.Single(c => c.ParkId == actualPark);
        var forecast = proposal.Forecast.Forecasts.Single(f => f.ParkId == actualPark);
        var score = proposal.Scores.SingleOrDefault(s => s.ParkId == actualPark);
        var actual = new RealizedDecision(attemptId, proposal.Context, candidate, forecast,
            score?.WaitSeconds ?? forecast.BaselineSeconds, score?.Method ?? "advisory-historical-reference");
        attempts[attemptId] = proposal.Context.PlanId;
        await trace.AppendAsync("RealizedDecision", actual, ct);
        await forecasts.CommitAsync(actual, ct);
        var now = proposal.Context.TimeMs;
        intents[proposal.Context.VehicleId] = new(proposal.Context.VehicleId, proposal.Context.PlanId, actualPark,
            now + (long)(candidate.Route.ApproachSeconds * 1000), candidate.ChargeEnergyKwh,
            now + (long)(candidate.Route.ApproachSeconds * 1000) + options.IntentGraceSeconds * 1000L, "ANNOUNCED");
        var park = proposal.Context.Parks.Single(p => p.Id == actualPark);
        lastPlans[proposal.Context.VehicleId] = (now, park.Queue, park.GridKw);
        await trace.AppendAsync("Intent", intents[proposal.Context.VehicleId], ct);
    }

    public async Task ReconcileAsync(IEnumerable<Vehicle> vehicles, long now, CancellationToken ct)
    {
        foreach (var vehicle in vehicles)
            if (intents.TryGetValue(vehicle.Id, out var intent) && (now >= intent.ExpiresMs || vehicle.Status is not VehicleStatus.ApproachingPark))
            {
                intents.Remove(vehicle.Id);
                await trace.AppendAsync("IntentClosed", intent with { Status = now >= intent.ExpiresMs ? "EXPIRED" : vehicle.Status.ToString() }, ct);
            }
    }

    public async Task OutcomeAsync(ObservedOutcome outcome, CancellationToken ct)
    {
        if (attempts.Remove(outcome.DecisionId, out var planId)) await trace.AppendAsync("Outcome", new { planId, outcome }, ct);
    }
    public Task SynchronizeAsync(long sequence, long now, CancellationToken ct) => forecasts.SynchronizeAsync(sequence, now, ct);
    public async Task CompleteAsync(long sequence, long now, CancellationToken ct)
    {
        // A terminal horizon/failure cancels promises that never became arrivals.
        // Closing these intentions changes no physical reservation or outcome.
        foreach (var intent in intents.Values.OrderBy(i => i.VehicleId, StringComparer.Ordinal))
            await trace.AppendAsync("IntentClosed", intent with { Status = "RUN_ENDED" }, ct);
        intents.Clear();
        await forecasts.CompleteAsync(sequence, now, ct);
        await trace.AppendAsync("Completed", new { decisions, Applied, MlApplied, Fallbacks, Replans,
            MaxCandidateSeconds, MaxForecastSeconds, MaxPlanningSeconds, MaxDecisionBarrierSeconds, remainingIntents = intents.Count }, ct);
    }
    public async ValueTask DisposeAsync() { await forecasts.DisposeAsync(); await trace.DisposeAsync(); }
}
