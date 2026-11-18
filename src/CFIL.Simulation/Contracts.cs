using CFIL.Domain;

namespace CFIL.Simulation;

public sealed record SimulationRun(string RunId, string ScenarioHash, string ModelVersion, int Seed);
public sealed record SimulationStep(long SimulationTimeMs, int ElapsedSeconds, IReadOnlyList<TrafficCommand> Commands);
public sealed record TrafficCommand(string Type, string VehicleId, string? ParkId = null, string? BayId = null);
public sealed record TrafficStepResult(long SimulationTimeMs, TrafficObservation[] Observations, string[] Entered,
    string[] Arrived, int Teleports, int Collisions, long ProviderWorkingSetBytes = 0, string[]? UnchangedVehicleIds = null);
public sealed record TrafficMetadata(string Provider, string Version, string PythonVersion, string Platform, string NetworkHash);
public interface ITrafficSimulator : IAsyncDisposable
{
    Task<TrafficMetadata> InitializeAsync(Scenario scenario, VehicleDefinition[] population, string outputDirectory, CancellationToken cancellationToken);
    Task<TrafficStepResult> StepAsync(SimulationStep step, CancellationToken cancellationToken);
}
public sealed record SimulationEvent(string SchemaVersion, string RunId, long Sequence, string EventId,
    long SimulationTimeMs, string EventType, string EntityId, string? CausationId, object Payload);
public sealed record VehicleSnapshot(string Id, string Status, double CapacityKwh, double EnergyKwh, double DistanceM,
    double PositionKm, string EdgeId, string? ParkId, string? BayId, double ChargedKwh, double ConsumedKwh);
public sealed record ParkSnapshot(string Id, double AvailableGridKw, Dictionary<string, string> Occupants, string[] Queue, string[] Inbound);
public sealed record SimulationSnapshot(string SchemaVersion, string RunId, long SimulationTimeMs, long LastEventSequence,
    VehicleSnapshot[] Vehicles, ParkSnapshot[] Parks);
public interface ISimulationEventSink
{
    Task AppendAsync(SimulationEvent simulationEvent, CancellationToken cancellationToken);
    Task SnapshotAsync(SimulationSnapshot snapshot, CancellationToken cancellationToken);
    Task TelemetryAsync(long timeMs, IReadOnlyList<VehicleSnapshot> vehicles, CancellationToken cancellationToken);
    Task FlushAsync(CancellationToken cancellationToken);
}
public sealed record DecisionContext(string DecisionId, string VehicleId, string Kind, long DecisionTimeMs, string? ParkId,
    double Soc, double PositionKm, double EstimatedArrivalSeconds, int OccupiedBays, int QueueLength,
    double AvailableGridKw, string Reason);
public sealed record ObservedOutcome(string DecisionId, long OutcomeTimeMs, string Status, double? WaitingSeconds, string? SessionId);
public sealed record RunResult(long SimulationTimeMs, long EventCount, object Metrics, SimulationSnapshot FinalSnapshot);
