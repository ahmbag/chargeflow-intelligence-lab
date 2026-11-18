using CFIL.Domain;

namespace CFIL.Simulation;

public sealed record EconomicOptions
{
    public string Version { get; init; } = "CFIL-05/1.0";
    public EconomicMode Mode { get; init; } = EconomicMode.ACCOUNTING_ONLY;
    public EconomicStrategy Strategy { get; init; } = EconomicStrategy.E0;
    public string TariffsPath { get; init; } = "";
    public string ProfilesPath { get; init; } = "";
    public decimal ReplanImprovementEur { get; init; } = 5;
    public decimal MaximumAdditionalSeconds { get; init; } = 1800;
    public decimal InventoryReferenceEurPerKwh { get; init; } = .5m;
    public decimal[] SensitivityTimeMultipliers { get; init; } = [.8m,1.2m];
    public void Validate(PlanningOptions? planning)
    {
        if (Version != "CFIL-05/1.0" || !Enum.IsDefined(Mode) || !Enum.IsDefined(Strategy) ||
            ReplanImprovementEur < 0 || MaximumAdditionalSeconds < 0 || InventoryReferenceEurPerKwh < 0 ||
            SensitivityTimeMultipliers.Length != 2 || SensitivityTimeMultipliers[0] is <= 0 or >= 1 || SensitivityTimeMultipliers[1] <= 1)
            throw new ArgumentException("Invalid economic options.");
        if (Mode == EconomicMode.OFF) return;
        if (string.IsNullOrWhiteSpace(TariffsPath) || string.IsNullOrWhiteSpace(ProfilesPath)) throw new ArgumentException("Economic catalogs required.");
        if (Mode is EconomicMode.ECONOMIC_ADVISORY or EconomicMode.ECONOMIC_CONTROLLED &&
            (planning is null || planning.Policy != PlanningPolicy.P2 || planning.Mode != PlanningMode.CONTROLLED || planning.DevelopmentTraining))
            throw new ArgumentException("Economic policies require the same frozen P2 controlled planning foundation.");
    }
}

public sealed record EconomicOption(string ParkId, EconomicCostEstimate Estimate, EconomicCostEstimate[] Sensitivities,
    long ExpectedSessionStartMs, bool WithinAdditionalTimeBudget);
public sealed record EconomicDecision(string PlanId, string VehicleId, long TimeMs, long SourceSequence,
    string Strategy, EconomicMode Mode, string? TechnicalParkId, string? RecommendedParkId, string? ExecutedParkId,
    string Reason, EconomicOption[] Options, string[] UnpricedParks, decimal StartingDistanceM,
    decimal TimeBudgetSeconds, string SensitivityMeaning = "WHAT_IF_REVALUATION_NOT_CONFIDENCE_INTERVAL");
public sealed record RealizedEconomicDecision(string AttemptId, EconomicDecision Decision, EconomicOption? SelectedOption);

public interface IEconomicPlanningLayer
{
    Task<PlanProposal> EvaluateAsync(PlanProposal proposal, Vehicle vehicle, PlanningOptions planning, CancellationToken ct);
    Task RealizedAsync(PlanProposal proposal, string attemptId, string actualPark, CancellationToken ct);
}
public interface IEconomicObservationSink
{
    Task ObserveAsync(long nowMs, long sourceSequence, IReadOnlyDictionary<string, Vehicle> vehicles,
        IReadOnlyList<ChargingSession> sessions, IReadOnlyList<ChargingParkState> parks, bool final, string runStatus, CancellationToken ct);
}

public static class EconomicPlanningRules
{
    public static (EconomicOption[] Options,string[] Unpriced,decimal TimeBudget) Options(PlanProposal proposal,
        VehicleDefinition vehicle, FleetEconomicProfile fleet, ITariffResolver resolver, EconomicOptions settings,
        IReadOnlyDictionary<string,string[]>? parkConnectors = null)
    {
        var calculator=new ChargingCostCalculator(); var evaluator=new EconomicCostEvaluator();
        var options=new List<EconomicOption>(); var unpriced=new List<string>();
        var fastest=proposal.Scores.Select(s=>(decimal)(s.TravelSeconds+s.WaitSeconds+s.ChargeSeconds)).DefaultIfEmpty(0).Min();
        var slack=Math.Max(0,(proposal.Context.Vehicle.TargetArrivalMs-proposal.Context.TimeMs)/1000m-fastest);
        var additional=Math.Min(settings.MaximumAdditionalSeconds,slack);
        foreach (var candidate in proposal.Candidates.Where(c=>c.Feasible))
        {
            var score=proposal.Scores.Single(s=>s.ParkId==candidate.ParkId);
            var start=checked(proposal.Context.TimeMs+(long)Math.Ceiling((candidate.Route.ApproachSeconds+score.WaitSeconds)*1000));
            var prices=resolver.Resolve(candidate.ParkId,parkConnectors is null?vehicle.Connectors:parkConnectors[candidate.ParkId],fleet,proposal.Context.TimeMs,start);
            if (prices.Length==0) { unpriced.Add(candidate.ParkId); continue; }
            // The existing target is battery-side; only the explicit efficiency conversion
            // turns it into an estimate at the authoritative grid-side meter boundary.
            var quantity=new BillingQuantities(ChargingCostCalculator.Meter(candidate.ChargeEnergyKwh/vehicle.Energy.Efficiency),
                (long)Math.Ceiling(candidate.ChargeSeconds*1000),0,true);
            var time=(decimal)(score.TravelSeconds+score.WaitSeconds+score.ChargeSeconds);
            var distance=(decimal)((candidate.Route.ApproachDistanceM+candidate.Route.ContinuationDistanceM)/1000);
            // Every strategy gets the same local optimization over complete bills,
            // including fixed and time fees, rather than just the headline kWh price.
            var bill=prices.Select(p=>calculator.Calculate(p,quantity)).OrderBy(b=>b.Total.Amount)
                .ThenBy(b=>b.Pricing.PaymentRouteId,StringComparer.Ordinal).ThenBy(b=>b.Pricing.TariffId,StringComparer.Ordinal).First();
            var estimate=evaluator.Evaluate(bill,fleet,time,distance,proposal.Context.TimeMs,proposal.Context.Vehicle.TargetArrivalMs);
            options.Add(new(candidate.ParkId,estimate,settings.SensitivityTimeMultipliers.Select(m=>evaluator.Evaluate(bill,fleet,time,distance,
                proposal.Context.TimeMs,proposal.Context.Vehicle.TargetArrivalMs,m)).ToArray(),start,time<=fastest+additional+.001m));
        }
        return (options.ToArray(),unpriced.ToArray(),additional);
    }

    public static (string? Park,string Reason) Select(PlanProposal proposal, EconomicOption[] options,
        EconomicOptions settings, PlanningOptions planning)
    {
        var context=proposal.Context;
        if (settings.Strategy==EconomicStrategy.E0) return (proposal.SelectedParkId,"TIME_FIRST_SAME_P2_PARK");
        var eligible=options.Where(o=>o.WithinAdditionalTimeBudget && proposal.Candidates.Any(c=>c.ParkId==o.ParkId && c.Feasible)).ToArray();
        decimal Value(EconomicOption o) => settings.Strategy==EconomicStrategy.E1 ? o.Estimate.EstimatedChargingBill.Total.Amount : o.Estimate.EconomicScore.Amount;
        var best=eligible.OrderBy(Value).ThenBy(o=>o.ParkId,StringComparer.Ordinal).FirstOrDefault();
        if (best is null) return (proposal.SelectedParkId,"NO_PRICED_BUDGET_OPTION_SAFE_TIME_FALLBACK");
        if (context.PreviousParkId is { } previous)
        {
            // Cooldown remains in the physical planner. Change count and commitment
            // distance still bind here; savings are measured in EUR, not seconds.
            if (context.Changes>=planning.MaxPlanChanges ||
                (context.Parks.Single(p=>p.Id==previous).PositionKm-context.Vehicle.PositionKm)*1000<=planning.CommitmentDistanceM)
                return (proposal.SelectedParkId,"RETAIN_TECHNICAL_COMMITMENT_GUARD");
            var current=eligible.SingleOrDefault(o=>o.ParkId==previous);
            if (current is not null && Value(current)-Value(best)<settings.ReplanImprovementEur) return (previous,"ECONOMIC_HYSTERESIS");
        }
        // Prices never create a physical candidate. If pricing is incomplete the
        // technical safe alternative remains available, never a 'drive on' command.
        return (best.ParkId,settings.Strategy==EconomicStrategy.E1?"MINIMUM_ACCESSIBLE_CHARGING_BILL":"MINIMUM_ECONOMIC_SCORE");
    }
}
