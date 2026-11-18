using CFIL.Domain;

namespace CFIL.Simulation;

public enum PlanningMode { DISABLED, ADVISORY, CONTROLLED }
public enum PlanningPolicy { P0, P1, P2, P3 }

public sealed record PlanningOptions
{
    public string Version { get; init; } = "CFIL-04/1.0";
    public PlanningMode Mode { get; init; } = PlanningMode.ADVISORY;
    public PlanningPolicy Policy { get; init; } = PlanningPolicy.P1;
    public string StateDirectory { get; init; } = "";
    public string? InitialModel { get; init; }
    public string? MaterializedInputs { get; init; }
    public bool DevelopmentTraining { get; init; }
    public double AuxiliaryReserveSeconds { get; init; } = 600;
    public double ReplanImprovementSeconds { get; init; } = 180;
    public int ReplanCooldownSeconds { get; init; } = 120;
    public int MaxPlanChanges { get; init; } = 2;
    public double CommitmentDistanceM { get; init; } = 500;
    public double SwitchPenaltySeconds { get; init; } = 120;
    public double LatenessWeight { get; init; } = 1;
    public double IntentPenaltySeconds { get; init; } = 60;
    public int IntentGraceSeconds { get; init; } = 900;
    public int ValidForSeconds { get; init; } = 5;
    public double ForecastTimeoutSeconds { get; init; } = 5;
    public string? Fault { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LearningStrategy { get; init; }

    public void Validate()
    {
        if (LearningStrategy is not (null or "M0" or "M1" or "M2") ||
            (LearningStrategy is not null && (Policy != PlanningPolicy.P2 || DevelopmentTraining || InitialModel is null)) ||
            Version != "CFIL-04/1.0" || !Enum.IsDefined(Mode) || !Enum.IsDefined(Policy) ||
            string.IsNullOrWhiteSpace(StateDirectory) || ReplanCooldownSeconds < 30 || MaxPlanChanges is < 0 or > 5 ||
            ValidForSeconds is < 1 or > 30 || IntentGraceSeconds < 0 ||
            new[] { AuxiliaryReserveSeconds, ReplanImprovementSeconds, CommitmentDistanceM, SwitchPenaltySeconds,
                LatenessWeight, IntentPenaltySeconds, ForecastTimeoutSeconds }.Any(n => !double.IsFinite(n) || n < 0) ||
            ForecastTimeoutSeconds is <= 0 or > 60 || CommitmentDistanceM < 100 ||
            (Policy is PlanningPolicy.P2 or PlanningPolicy.P3 && InitialModel is null) ||
            (Policy == PlanningPolicy.P0 && Mode != PlanningMode.DISABLED) ||
            Fault is not (null or "forecast-timeout-once" or "forecast-kill-once" or "after-route-once"))
            throw new ArgumentException("Invalid versioned planning configuration.");
    }
}

// Only present observations and static geometry cross this boundary. CapacityChange,
// planned PKW departures and future SUMO edge weights are deliberately absent.
public sealed record PlanningVehicle(string Id, string TripId, string EdgeId, double PositionKm, double EnergyKwh,
    double Soc, double CapacityKwh, double MaxChargingKw, double DesiredSpeedMps, double GrossMassKg,
    double AuxiliaryKw, double ReserveKwh, double RouteEndKm, long TargetArrivalMs, string State);
public sealed record PlanningPark(string Id, double PositionKm, double GridKw, int Queue, int Occupied,
    int CompatibleBays, int BlockedCompatibleBays, double ConnectorKw, string[] CompatibleBayIds);
public sealed record ArrivalIntent(string VehicleId, string PlanId, string ParkId, long EstimatedArrivalMs,
    double EnergyKwh, long ExpiresMs, string Status);
public sealed record RouteCandidate(string ParkId, bool Legal, string? Rejection, string CurrentEdge,
    double CurrentLanePositionM, string[] CurrentRoute, string[] RouteEdges, double ApproachDistanceM,
    double ApproachSeconds, double ContinuationDistanceM, double ContinuationSeconds, long QueriedAtMs);
public sealed record ChargingDecisionContext(string Version, string RunId, string DecisionId, string VehicleId,
    string TripId, string PlanId, int ExpectedRevision, int Revision, long TimeMs, long SourceSequence,
    long ValidUntilMs, PlanningMode Mode, PlanningPolicy Policy, string Trigger, PlanningVehicle Vehicle,
    PlanningPark[] Parks, ArrivalIntent[] Intents, string? PreviousParkId, int Changes);
public sealed record ChargingCandidate(string ParkId, RouteCandidate Route, bool Feasible, string[] Rejections,
    double EnergyToQueueKwh, double AuxiliaryReserveKwh, double MinimumReserveKwh, double ArrivalSoc,
    double TargetSoc, double ChargeEnergyKwh, double AssumedGridKw, double ChargeSeconds,
    double ContinuationEnergyKwh, double DeterministicWaitSeconds, double CoordinationSeconds,
    Dictionary<string, double> Features);
public sealed record WaitForecast(string ParkId, double WaitSeconds, double BaselineSeconds, double? RawSeconds,
    string Method, string? FallbackReason, string FeatureVersion, string ModelVersion, int Revision,
    long SourceSequence, bool Supported);
public sealed record ForecastBatch(string DecisionId, int Revision, long SourceSequence, string StateHash,
    WaitForecast[] Forecasts);
public sealed record CandidateScore(string ParkId, double TravelSeconds, double WaitSeconds, double ChargeSeconds,
    double LatenessSeconds, double SwitchSeconds, double CoordinationSeconds, double TotalSeconds, string Method);
public sealed record PlanProposal(ChargingDecisionContext Context, ChargingCandidate[] Candidates, ForecastBatch Forecast,
    CandidateScore[] Scores, string? SelectedParkId, string Reason);
public sealed record PlanApplication(string PlanId, string VehicleId, int ExpectedRevision, int Revision,
    long ValidUntilMs, RouteCandidate Route, string? Fault = null);
public sealed record PlanReceipt(string PlanId, string VehicleId, int Revision, string Status, string? Error,
    string CurrentEdge, string[] ActualRoute, string[] ActualStops, string[] CompletedOperations, bool Duplicate, int RouteIndex = 0);
public sealed record RealizedDecision(string AttemptId, ChargingDecisionContext Context, ChargingCandidate Candidate,
    WaitForecast Forecast, double IssuedWaitSeconds, string IssuedMethod);

public interface IPlanningTraffic
{
    Task<RouteCandidate[]> FindChargingRoutesAsync(string vehicleId, string[] parkIds, CancellationToken ct);
    Task<PlanReceipt> ApplyPlanAsync(PlanApplication application, CancellationToken ct);
}
public interface IWaitForecastProvider : IAsyncDisposable
{
    Task SynchronizeAsync(long sourceSequence, long timeMs, CancellationToken ct);
    Task<ForecastBatch> ForecastAsync(ChargingDecisionContext context, ChargingCandidate[] candidates, CancellationToken ct);
    Task CommitAsync(RealizedDecision decision, CancellationToken ct);
    Task CompleteAsync(long sourceSequence, long timeMs, CancellationToken ct);
}
public interface IDecisionTrace : IAsyncDisposable
{
    Task AppendAsync(string type, object payload, CancellationToken ct);
}
