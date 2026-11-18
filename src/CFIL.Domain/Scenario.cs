namespace CFIL.Domain;

public sealed record HighwayNetwork(double LengthKm = 240, int Lanes = 2, double SpeedLimitMps = 36.11,
    string Direction = "eastbound");

public sealed record CapacityChange(int StartSeconds, int EndSeconds, double GridFactor, string? BlockedBayId = null);
public sealed record ChargingConnector(string Id, string Type, double MaxGridPowerKw);
public sealed record ChargingBay(string Id, double LengthM, double WidthM, double MaxTrailerLengthM,
    double TurningRadiusM, bool DriveThrough, ChargingConnector Connector, string[] Conflicts,
    string[] ExitBlockedBy);
public sealed record ChargingPark(string Id, double PositionKm, double GridPowerKw, double AccessWidthM,
    int QueueCapacity, ChargingBay[] Bays, CapacityChange[] Changes);

public sealed record Scenario
{
    public string SchemaVersion { get; init; } = "1.0";
    public string Id { get; init; } = "BASELINE-V1";
    public int Seed { get; init; } = 42;
    public int DurationSeconds { get; init; } = 86400;
    public int StepSeconds { get; init; } = 1;
    public int TruckCount { get; init; } = 50;
    public int CarCount { get; init; } = 1000;
    public int DepartureWindowSeconds { get; init; } = 43200;
    public int TelemetryIntervalSeconds { get; init; } = 300;
    public int SnapshotIntervalSeconds { get; init; } = 3600;
    public int MaxQueueWaitSeconds { get; init; } = 5400;
    public int ConnectingSeconds { get; init; } = 30;
    public int DeadlockTimeoutSeconds { get; init; } = 600;
    public string Policy { get; init; } = "nearest-compatible-v1";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MultiDaySchedule? MultiDay { get; init; }
    public HighwayNetwork Network { get; init; } = new();
    public ChargingPark[] Parks { get; init; } = [];

    public void Validate()
    {
        static void Require(bool valid, string message) { if (!valid) throw new ArgumentException(message); }
        static bool Positive(double n) => double.IsFinite(n) && n > 0;
        Require(SchemaVersion == "1.0", "Unsupported scenario schema.");
        Require(System.Text.RegularExpressions.Regex.IsMatch(Id, "^[A-Za-z0-9-]+$"), "Invalid scenario ID.");
        Require(Seed >= 0 && DurationSeconds > 0 && DurationSeconds <= 604800 && StepSeconds == 1,
            "Seed, duration or step invalid; Foundation uses one-second steps.");
        Require(TruckCount >= 0 && CarCount >= 0 && TruckCount + CarCount is > 0 and <= 10000, "Invalid population.");
        Require(DepartureWindowSeconds > 0 && DepartureWindowSeconds < DurationSeconds, "Invalid departure window.");
        Require(TelemetryIntervalSeconds > 0 && SnapshotIntervalSeconds > 0 && MaxQueueWaitSeconds > 0 &&
            ConnectingSeconds >= 0 && DeadlockTimeoutSeconds > 0, "Invalid timing.");
        Require(Policy == "nearest-compatible-v1", "Unknown charging policy.");
        MultiDay?.Validate(this);
        Require(Positive(Network.LengthKm) && Network.LengthKm >= 10 && Network.Lanes >= 2 &&
            Positive(Network.SpeedLimitMps) && Network.Direction == "eastbound", "Unsupported network.");
        Require(Parks.Length > 0 && Parks.Select(p => p.Id).Distinct().Count() == Parks.Length, "Duplicate or missing parks.");
        double previous = 0;
        foreach (var p in Parks.OrderBy(p => p.PositionKm))
        {
            Require(System.Text.RegularExpressions.Regex.IsMatch(p.Id, "^[A-Za-z0-9-]+$"), "Invalid park ID.");
            Require(double.IsFinite(p.PositionKm) && p.PositionKm > previous + 2 && p.PositionKm < Network.LengthKm - 2,
                "Parks must be separated and inside the network.");
            previous = p.PositionKm;
            Require(Positive(p.GridPowerKw) && Positive(p.AccessWidthM) && p.QueueCapacity > 0 && p.Bays.Length > 0,
                "Invalid park capacities.");
            var ids = p.Bays.Select(b => b.Id).ToHashSet();
            Require(ids.Count == p.Bays.Length && p.Bays.Select(b => b.Connector.Id).Distinct().Count() == p.Bays.Length,
                "Duplicate bay or connector.");
            foreach (var b in p.Bays)
            {
                Require(System.Text.RegularExpressions.Regex.IsMatch(b.Id, "^[A-Za-z0-9-]+$"), "Invalid bay ID.");
                Require(Positive(b.LengthM) && Positive(b.WidthM) && b.MaxTrailerLengthM >= 0 && double.IsFinite(b.MaxTrailerLengthM)
                    && Positive(b.TurningRadiusM) && Positive(b.Connector.MaxGridPowerKw), "Invalid geometry/connector.");
                Require(b.Connector.Type is "CCS" or "MCS", "Unknown connector.");
                Require(b.Conflicts.Concat(b.ExitBlockedBy).All(x => ids.Contains(x) && x != b.Id), "Invalid conflict reference.");
            }
            foreach (var c in p.Changes)
                Require(c.StartSeconds >= 0 && c.EndSeconds > c.StartSeconds && c.EndSeconds <= DurationSeconds &&
                    double.IsFinite(c.GridFactor) && c.GridFactor is >= 0 and <= 1 &&
                    (c.BlockedBayId is null || ids.Contains(c.BlockedBayId)), "Invalid capacity change.");
        }
    }
}
