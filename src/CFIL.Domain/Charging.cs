namespace CFIL.Domain;

public sealed record QueueEntry(string VehicleId, long ArrivalMs, string DecisionId);
public sealed class ChargingSession(string id, string vehicleId, string parkId, string bayId, string decisionId,
    long queueArrivalMs, long bayReservedMs)
{
    public string Id { get; } = id;
    public string VehicleId { get; } = vehicleId;
    public string ParkId { get; } = parkId;
    public string BayId { get; } = bayId;
    public string DecisionId { get; } = decisionId;
    public long QueueArrivalMs { get; } = queueArrivalMs;
    public long BayReservedMs { get; } = bayReservedMs;
    public long? StartedMs { get; set; }
    public long? EndedMs { get; set; }
    public long? ReleasedMs { get; set; }
    public double AssignedGridKw { get; set; }
    public double BatteryEnergyKwh { get; set; }
    public double GridEnergyKwh { get; set; }
    public string Outcome { get; set; } = "pending";
}

public sealed class ChargingParkState(ChargingPark definition)
{
    public ChargingPark Definition { get; } = definition;
    public Dictionary<string, string> BayOccupants { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Inbound { get; } = new(StringComparer.Ordinal);
    public List<QueueEntry> Queue { get; } = [];
    public HashSet<string> BlockedBays { get; } = [];
    public double AvailableGridKw { get; private set; } = definition.GridPowerKw;
    public double GridEnergyKwh { get; set; }
    public double BayOccupiedSeconds { get; set; }
    public double ConnectorActiveSeconds { get; set; }
    public double AvailableGridKwh { get; set; }
    public int MaxQueueLength { get; set; }

    public bool UpdateCapacity(long timeMs)
    {
        var active = Definition.Changes.Where(c => timeMs >= c.StartSeconds * 1000L && timeMs < c.EndSeconds * 1000L).ToArray();
        var power = Definition.GridPowerKw * active.Select(c => c.GridFactor).DefaultIfEmpty(1).Min();
        var blocked = active.Where(c => c.BlockedBayId is not null).Select(c => c.BlockedBayId!).ToHashSet();
        var changed = Math.Abs(power - AvailableGridKw) > 1e-9 || !blocked.SetEquals(BlockedBays);
        AvailableGridKw = power;
        BlockedBays.Clear(); BlockedBays.UnionWith(blocked);
        return changed;
    }
    public bool Compatible(VehicleDefinition vehicle, ChargingBay bay) => vehicle.LengthM <= bay.LengthM &&
        vehicle.WidthM <= Math.Min(bay.WidthM, Definition.AccessWidthM) && vehicle.TrailerLengthM <= bay.MaxTrailerLengthM &&
        vehicle.TurningRadiusM <= bay.TurningRadiusM && (bay.DriveThrough || vehicle.TrailerLengthM == 0) &&
        vehicle.Connectors.Contains(bay.Connector.Type);

    public string? UnavailableReason(VehicleDefinition vehicle, ChargingBay bay)
    {
        if (!Compatible(vehicle, bay)) return "incompatible-geometry-or-connector";
        if (BlockedBays.Contains(bay.Id) || BayOccupants.ContainsKey(bay.Id)) return "physical-capacity";
        if (bay.ExitBlockedBy.Any(BlockedBays.Contains)) return "blocked-access";
        // Conflict edges are treated symmetrically even if a scenario lists them on only one side.
        // Exit dependencies are also reserved: admitting a neighbour must never trap an occupied bay.
        foreach (var occupied in Definition.Bays.Where(b => BayOccupants.ContainsKey(b.Id)))
            if (bay.Conflicts.Contains(occupied.Id) || occupied.Conflicts.Contains(bay.Id) ||
                bay.ExitBlockedBy.Contains(occupied.Id) || occupied.ExitBlockedBy.Contains(bay.Id)) return "blocked-access";
        if (AvailableGridKw <= 0) return "electrical-capacity";
        return null;
    }
    public ChargingBay? FindFreeBay(VehicleDefinition vehicle) => Definition.Bays.OrderBy(b => b.Id, StringComparer.Ordinal)
        .FirstOrDefault(b => UnavailableReason(vehicle, b) is null);
    public void Reserve(VehicleDefinition vehicle, ChargingBay bay)
    {
        var reason = UnavailableReason(vehicle, bay);
        if (reason is not null) throw new InvalidOperationException($"Cannot reserve {bay.Id}: {reason}");
        BayOccupants.Add(bay.Id, vehicle.Id);
    }
    public bool CanExit(string bayId) => Definition.Bays.Single(b => b.Id == bayId).ExitBlockedBy
        .All(id => !BayOccupants.ContainsKey(id) && !BlockedBays.Contains(id));
    public bool HasQueueSpace => Queue.Count + Inbound.Count < Definition.QueueCapacity;
}

public interface IChargingPowerAllocator
{
    IReadOnlyDictionary<string, double> Allocate(double availableGridKw, IReadOnlyDictionary<string, double> requests);
}
public sealed class FairPowerAllocator : IChargingPowerAllocator
{
    public IReadOnlyDictionary<string, double> Allocate(double availableGridKw, IReadOnlyDictionary<string, double> requests)
    {
        if (!double.IsFinite(availableGridKw) || availableGridKw < 0 || requests.Values.Any(x => !double.IsFinite(x) || x < 0))
            throw new ArgumentOutOfRangeException(nameof(availableGridKw));
        var result = requests.Keys.Order(StringComparer.Ordinal).ToDictionary(k => k, _ => 0d, StringComparer.Ordinal);
        var pending = result.Keys.ToList();
        var remaining = availableGridKw;
        // Progressive water filling redistributes unused shares when a vehicle tapers or has a small port.
        // Sorting IDs fixes all tie breaks and floating-point accumulation order for scenario replay.
        while (pending.Count > 0 && remaining > 1e-10)
        {
            var share = remaining / pending.Count;
            var capped = pending.Where(k => requests[k] <= share).ToArray();
            if (capped.Length == 0)
            {
                foreach (var k in pending) result[k] = share;
                break;
            }
            foreach (var k in capped) { result[k] = requests[k]; remaining -= requests[k]; pending.Remove(k); }
        }
        return result;
    }
}

public sealed record ChargingDecision(string? ParkId, string Reason, double EstimatedArrivalSeconds);
public interface IChargingDecisionPolicy
{
    ChargingDecision Decide(Vehicle vehicle, IReadOnlyList<ChargingParkState> parks);
}
public sealed class NearestCompatiblePolicy : IChargingDecisionPolicy
{
    public ChargingDecision Decide(Vehicle vehicle, IReadOnlyList<ChargingParkState> parks)
    {
        var d = vehicle.Definition;
        var reserve = d.Logistics?.RequiredReserveKwh ?? d.CapacityKwh * .05;
        var consumption = d.Energy.ConsumptionAt(d.DesiredSpeedMps, d.GrossMassKg) + d.Energy.AuxiliaryKw / (d.DesiredSpeedMps * 3.6);
        var remaining = Math.Max(0, d.RouteEndKm - vehicle.PositionKm);
        var candidates = parks.Where(p => p.Definition.PositionKm > vehicle.PositionKm + .5 &&
            p.Definition.PositionKm < d.RouteEndKm && !vehicle.VisitedParks.Contains(p.Definition.Id) &&
            p.Definition.Bays.Any(b => p.Compatible(d, b))).OrderBy(p => p.Definition.PositionKm).ToArray();
        // A simple driver need not refill for the entire remaining highway at the first park.
        // Skip the nearest opportunity only if the next compatible one (or destination) is reachable
        // with reserve. This spreads uncoordinated demand naturally without using future occupancy.
        var nextOpportunity = candidates.Length > 1 ? candidates[1].Definition.PositionKm - vehicle.PositionKm + 2 : remaining;
        if (vehicle.Battery.Soc > d.Energy.ChargingThresholdSoc && vehicle.Battery.EnergyKwh >= nextOpportunity * consumption + reserve)
            return new(null, "sufficient-next-opportunity-reserve", 0);
        foreach (var p in parks.OrderBy(p => p.Definition.PositionKm))
        {
            var distance = p.Definition.PositionKm - vehicle.PositionKm;
            if (distance < .5 || p.Definition.PositionKm >= d.RouteEndKm || vehicle.VisitedParks.Contains(p.Definition.Id)) continue;
            if (!p.Definition.Bays.Any(b => p.Compatible(d, b))) continue;
            if ((distance + 2) * consumption + reserve > vehicle.Battery.EnergyKwh) continue;
            // Do not reserve a scarce queue slot hours before physical arrival. The baseline makes
            // a diversion at the five-kilometre decision horizon, upstream of the actual exit.
            if (distance > 5) return new(null, "outside-decision-horizon", 0);
            return new(p.Definition.Id, d.Kind == VehicleKind.Truck ? "nearest-compatible-with-reserve" : "uncoordinated-nearest",
                (distance + 1) * 1000 / d.DesiredSpeedMps);
        }
        return new(null, vehicle.Battery.EnergyKwh < remaining * consumption ? "no-feasible-route" : "no-stop-required", 0);
    }
}
