namespace CFIL.Domain;

public enum VehicleKind { Car, Truck }
public enum VehicleStatus { Scheduled, Driving, ApproachingPark, Queued, Connecting, Charging, Departing, Stopping, Arrived, Stranded }

public sealed class BatteryState
{
    public double CapacityKwh { get; }
    public double EnergyKwh { get; private set; }
    public double Soc => EnergyKwh / CapacityKwh;
    public BatteryState(double capacityKwh, double energyKwh)
    {
        if (!double.IsFinite(capacityKwh) || capacityKwh <= 0 || !double.IsFinite(energyKwh) || energyKwh < 0 || energyKwh > capacityKwh)
            throw new ArgumentOutOfRangeException(nameof(energyKwh));
        CapacityKwh = capacityKwh; EnergyKwh = energyKwh;
    }
    public bool Consume(double kwh)
    {
        if (!double.IsFinite(kwh) || kwh < 0) throw new ArgumentOutOfRangeException(nameof(kwh));
        var sufficient = EnergyKwh + 1e-10 >= kwh;
        EnergyKwh = Math.Max(0, EnergyKwh - kwh);
        return sufficient;
    }
    public double Charge(double gridKw, double efficiency, double seconds, double targetSoc = 1)
    {
        if (!double.IsFinite(gridKw) || gridKw < 0 || !double.IsFinite(efficiency) || efficiency is <= 0 or > 1 ||
            !double.IsFinite(seconds) || seconds < 0 || !double.IsFinite(targetSoc) || targetSoc is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(gridKw));
        var accepted = Math.Min(Math.Max(0, CapacityKwh * targetSoc - EnergyKwh), gridKw * efficiency * seconds / 3600);
        EnergyKwh += accepted;
        return accepted;
    }
}

public sealed record VehicleEnergyProfile(double ConsumptionKwhPerKm, double MaxGridChargingKw, double Efficiency,
    double AuxiliaryKw, double TargetSoc, double ChargingThresholdSoc)
{
    // All power limits are grid-side. Efficiency is applied exactly once when energy enters the battery.
    public double AcceptanceKw(double soc) => MaxGridChargingKw * (soc <= .6 ? 1 : Math.Max(.1, 1 - (soc - .6) * 2.25));
    public double ConsumptionAt(double speedMps, double massKg) => ConsumptionKwhPerKm *
        (0.8 + 0.2 * Math.Pow(speedMps / 25, 2)) * (1 + Math.Max(0, massKg - 20000) / 200000);
}
public sealed record TruckLogisticsProfile(double PayloadKg, long TargetArrivalMs, double DrivingBudgetSeconds,
    double RequiredReserveKwh, double RequiredBreakSeconds);
public sealed record VehicleDefinition(string Id, VehicleKind Kind, int DepartureSeconds, double DesiredSpeedMps,
    double LengthM, double WidthM, double TrailerLengthM, double TurningRadiusM, double GrossMassKg,
    string[] Connectors, double CapacityKwh, double InitialSoc, VehicleEnergyProfile Energy,
    TruckLogisticsProfile? Logistics, double RouteEndKm);

public sealed class Vehicle(VehicleDefinition definition)
{
    public VehicleDefinition Definition { get; } = definition;
    public string Id => Definition.Id;
    public BatteryState Battery { get; } = new(definition.CapacityKwh, definition.CapacityKwh * definition.InitialSoc);
    public VehicleStatus Status { get; private set; } = VehicleStatus.Scheduled;
    public double DistanceM { get; set; }
    public double PositionKm { get; set; }
    public double SpeedMps { get; set; }
    public string EdgeId { get; set; } = "";
    public double ConsumedKwh { get; set; }
    public double ChargedKwh { get; set; }
    public double DrivingSeconds { get; set; }
    public double StationarySeconds { get; set; }
    public double AccessStoppedSeconds { get; set; }
    public double BreakSeconds { get; set; }
    public long? EnteredMs { get; set; }
    public long? ArrivedMs { get; set; }
    public string? ParkId { get; set; }
    public string? BayId { get; set; }
    public long QueueArrivalMs { get; set; }
    public long ConnectedAtMs { get; set; }
    public long? ExitBlockedSinceMs { get; set; }
    public string? DecisionId { get; set; }
    public string? LastEventId { get; set; }
    public string? StrandingReason { get; set; }
    public HashSet<string> VisitedParks { get; } = [];
    public double RangeKm => Battery.EnergyKwh / Definition.Energy.ConsumptionAt(Math.Max(1, SpeedMps), Definition.GrossMassKg);
    public void Transition(VehicleStatus next)
    {
        var valid = (Status, next) switch
        {
            (VehicleStatus.Scheduled, VehicleStatus.Driving) => true,
            (VehicleStatus.Driving, VehicleStatus.ApproachingPark or VehicleStatus.Arrived or VehicleStatus.Stranded or VehicleStatus.Stopping) => true,
            (VehicleStatus.ApproachingPark, VehicleStatus.Queued or VehicleStatus.Departing or VehicleStatus.Stranded or VehicleStatus.Stopping) => true,
            (VehicleStatus.Queued, VehicleStatus.Connecting or VehicleStatus.Departing or VehicleStatus.Stranded or VehicleStatus.Stopping) => true,
            (VehicleStatus.Connecting, VehicleStatus.Charging or VehicleStatus.Stranded or VehicleStatus.Stopping) => true,
            (VehicleStatus.Charging, VehicleStatus.Departing or VehicleStatus.Stranded or VehicleStatus.Stopping) => true,
            (VehicleStatus.Departing, VehicleStatus.Driving or VehicleStatus.Stranded or VehicleStatus.Stopping) => true,
            (VehicleStatus.Stopping, VehicleStatus.Stranded) => true,
            _ => false
        };
        if (!valid) throw new InvalidOperationException($"Invalid vehicle transition {Id}: {Status} -> {next}");
        Status = next;
    }
}

public static class PopulationFactory
{
    public static VehicleDefinition[] Create(Scenario scenario)
    {
        if (scenario.MultiDay is not null) return MultiDayPopulation.Create(scenario);
        // SplitMix64 gives a documented, runtime-independent random sequence (unlike System.Random).
        var random = new StableRandom((ulong)scenario.Seed);
        var vehicles = new List<VehicleDefinition>();
        for (var i = 0; i < scenario.TruckCount + scenario.CarCount; i++)
        {
            var truck = i < scenario.TruckCount;
            var kind = truck ? VehicleKind.Truck : VehicleKind.Car;
            var id = $"{(truck ? "TRUCK" : "CAR")}-{(truck ? i : i - scenario.TruckCount) + 1:D4}";
            var depart = (int)(random.Next() * scenario.DepartureWindowSeconds);
            var capacity = truck ? 450 + random.Next() * 350 : 40 + random.Next() * 70;
            var soc = .35 + random.Next() * .50;
            var speed = truck ? 21 + random.Next() * 4 : 26 + random.Next() * 10;
            var trailer = truck ? 10 + random.Next() * 4 : 0;
            var payload = truck ? 5000 + random.Next() * 20000 : 0;
            var energy = new VehicleEnergyProfile(truck ? 1.05 + random.Next() * .4 : .15 + random.Next() * .09,
                truck ? 350 + random.Next() * 400 : 75 + random.Next() * 175, .94,
                truck ? 2 : .6, .8 + random.Next() * .1, .35 + random.Next() * .15);
            var end = random.Next() < .2 ? scenario.Network.LengthKm - 20 : scenario.Network.LengthKm;
            vehicles.Add(new(id, kind, depart, speed, truck ? 5.5 + trailer : 4 + random.Next(), truck ? 2.55 : 1.85,
                trailer, truck ? 12 : 5, truck ? 14000 + payload : 1800, truck ? ["CCS", "MCS"] : ["CCS"],
                capacity, soc, energy, truck ? new(payload, (depart + (long)(end * 1000 / speed) + 3600) * 1000,
                    16200, capacity * .08, 2700) : null, end));
        }
        return vehicles.OrderBy(v => v.Id, StringComparer.Ordinal).ToArray();
    }
    private sealed class StableRandom(ulong seed)
    {
        private ulong state = seed;
        public double Next()
        {
            unchecked
            {
                var z = state += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return ((z ^ (z >> 31)) >> 11) * (1.0 / (1UL << 53));
            }
        }
    }
}
