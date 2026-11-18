using System.Text.Json;
using CFIL.Domain;

namespace CFIL.Observation;

public sealed record SourceReference(string Artifact, long Sequence, long TimeMs, string Version = "CFIL06-READ-V1");
public sealed record RunOverview(string Key, string Name, string Group, string RunId, int Seed, string Scenario,
    string Policy, string EconomicsMode, string Status, long DurationMs, string ModelVersion, string CodeHash,
    int Trucks, int Cars, string Integrity, string[] Artifacts, string[] Missing, string Origin = "SYNTHETIC");
public sealed record Point(double X, double Y);
public sealed record NetworkEdge(string Id, Point[] Shape, int Lanes, bool Internal);
public sealed record NetworkView(NetworkEdge[] Edges, double MinX, double MinY, double MaxX, double MaxY,
    string PositionMeaning = "Observed longitudinal position projected onto the recorded edge. Exact lane and lateral position were not archived.");
public sealed record VehicleView(string Id, string Kind, string Status, double Soc, double EnergyKwh, double DistanceM,
    double X, double Y, double Heading, string EdgeId, string? ParkId, string? BayId, long PositionTimeMs,
    long EnergyTimeMs, bool Stale, string PositionQuality, double LengthM);
public sealed record BayView(string Id, string? Occupant, double LengthM, double WidthM, double TrailerM,
    double TurningRadiusM, bool DriveThrough, string Connector, double ConnectorLimitKw, string[] Conflicts,
    string[] ExitBlockedBy, bool Blocked);
public sealed record ChargingParkStatus(string Id, double X, double Y, double AccessWidthM, int QueueCapacity,
    double ConfiguredGridKw, double AvailableGridKw, double AssignedPowerKw, double? ObservedMeanGridKw,
    string PowerMeaning, string[] Queue, string[] Inbound, string[] Intents, BayView[] Bays, SourceReference Source);
public sealed record SceneSnapshot(string RunKey, string RunId, long Generation, long CursorMs, long ProducerTimeMs,
    long DisplayedTimeMs, long SourceSequence, string ProjectionVersion, DateTimeOffset ReceivedUtc,
    DateTimeOffset? ProducerPublishedUtc, string Mode, string Delivery, string Health, VehicleView[] Vehicles,
    ChargingParkStatus[] Parks, Dictionary<string,int> Counts, string[] Gaps, SourceReference Source);
public sealed record TimelineEntry(long TimeMs, long Sequence, string Type, JsonElement Facts, SourceReference Source);
public sealed record DecisionDetail(string Id, string PlanId, string VehicleId, long TimeMs, long SourceSequence,
    string? SelectedPark, string Reason, string Explanation, JsonElement Context, JsonElement Candidates,
    JsonElement Scores, JsonElement Forecast, JsonElement? Economics, TimelineEntry[] Execution,
    TimelineEntry[] LaterOutcomes, string[] Limits);
public sealed record VehicleTimeline(string RunKey, long Generation, long CursorMs, VehicleDefinition Definition,
    VehicleView Current, TimelineEntry[] Events, DecisionDetail[] Decisions, string[] CurrentRoute,
    string[] DataGaps, SourceReference Source);
public sealed record CostReconciliationView(string RunKey, long Generation, string VehicleId, long CursorMs,
    bool Retrospective, string Availability, JsonElement? VehicleAssessment, JsonElement[] Sessions,
    JsonElement[] Reconciliations, string Denominator, string[] Notes, SourceReference Source);
public sealed record LearningRow(string VehicleId, long PredictionTimeMs, long Revision, string Method,
    double? PredictionSeconds, long? LabelAvailableMs, double? LabelSeconds, string Status);
public sealed record LearningView(string Policy, bool Frozen, long? InitialRevision, long? CurrentRevision,
    long? OnlineUpdates, int TruckLabels, int CarLabels, string MetricAvailability, JsonElement? Metrics,
    LearningRow[] Rows, string Drift, string[] Notes, JsonElement? MultiDay = null);
public sealed record PairComparison(bool Comparable, string[] Reasons, string LeftKey, string RightKey,
    int PlannedTrucks, string Denominator, Dictionary<string,Money> MoneyDifferences,
    Dictionary<string,decimal> OperationalDifferences, JsonElement? FormalQuality, string SubsetNotice,
    string[] DifferentVehicles, long? FirstLifecycleDifferenceMs, string DifferenceScope);
public sealed record ProjectionStatistics(int ActiveRuns, long SourceBytesRead, long SourcePasses,
    long CriticalEvents, long SourceRecords, long FramePointers, int MaxPendingPerViewer = 1,
    long FrameLoads = 0,int SnapshotFrames = 0,int TelemetryFrames = 0);

public static class J
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static JsonElement? Get(this JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key,out var p) ? p : null;
    public static string Text(this JsonElement value, string key, string fallback = "") => value.Get(key) is { ValueKind: JsonValueKind.String } p ? p.GetString()! : fallback;
    public static long Long(this JsonElement value, string key, long fallback = 0) => value.Get(key) is { ValueKind: JsonValueKind.Number } p && p.TryGetInt64(out var n) ? n : fallback;
    public static double Num(this JsonElement value, string key, double fallback = 0) => value.Get(key) is { ValueKind: JsonValueKind.Number } p ? p.GetDouble() : fallback;
    public static bool Flag(this JsonElement value,string key) => value.Get(key) is { ValueKind: JsonValueKind.True };
    public static JsonElement[] Items(this JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    public static JsonElement Empty => JsonSerializer.SerializeToElement(new {}, Options);
    public static JsonElement Array => JsonSerializer.SerializeToElement(System.Array.Empty<object>(), Options);
    public static JsonElement Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document=JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
}
