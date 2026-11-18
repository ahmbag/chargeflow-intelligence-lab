using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CFIL.Simulation;

namespace CFIL.Infrastructure;

public sealed record ReplayReport(string RunId, long Events, long LastTimeMs, int Snapshots, int Decisions,
    int Outcomes, string EventHash, string SnapshotHash);
public static class ReplayReader
{
    public static ReplayReport Validate(string directory)
    {
        long sequence = 0, time = 0;
        string? runId = null;
        var decisionTimes = new Dictionary<string, long>(StringComparer.Ordinal);
        var outcomes = new HashSet<string>(StringComparer.Ordinal);
        var lastEntityEvent = new Dictionary<string, string>(StringComparer.Ordinal);
        var eventTimes = new List<long> { 0 };
        foreach (var line in File.ReadLines(Path.Combine(directory, "events.jsonl")))
        {
            var e = JsonDefaults.Deserialize<SimulationEvent>(line);
            runId ??= e.RunId;
            if (e.SchemaVersion != "1.0" || e.RunId != runId || e.Sequence != ++sequence || e.SimulationTimeMs < time ||
                e.EventId != $"{runId}:{sequence:D9}" || string.IsNullOrWhiteSpace(e.EventType) || string.IsNullOrWhiteSpace(e.EntityId))
                throw new InvalidDataException("Corrupt event envelope or sequence.");
            if (e.CausationId is not null && (!lastEntityEvent.TryGetValue(e.EntityId, out var prior) || prior != e.CausationId))
                throw new InvalidDataException("Invalid event causation.");
            lastEntityEvent[e.EntityId] = e.EventId;
            time = e.SimulationTimeMs; eventTimes.Add(time);
            var payload = (JsonElement)e.Payload;
            if (e.EventType == "ChargingDecisionMade")
            {
                var id = payload.GetProperty("decisionId").GetString()!;
                if (payload.GetProperty("decisionTimeMs").GetInt64() != time || !decisionTimes.TryAdd(id, time))
                    throw new InvalidDataException("Invalid decision context.");
            }
            if (e.EventType == "ChargingOutcomeObserved")
            {
                var id = payload.GetProperty("decisionId").GetString()!;
                if (!decisionTimes.TryGetValue(id, out var decisionTime) || decisionTime > time || !outcomes.Add(id) ||
                    payload.GetProperty("outcomeTimeMs").GetInt64() != time)
                    throw new InvalidDataException("Outcome precedes its decision or is duplicated.");
                var wait = payload.GetProperty("waitingSeconds");
                if (payload.GetProperty("status").GetString() == "observed" ? wait.ValueKind != JsonValueKind.Number || wait.GetDouble() < 0 : wait.ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException("Invalid observed/censored label.");
            }
        }
        if (sequence == 0) throw new InvalidDataException("Empty event log.");
        var count = 0; long snapshotTime = -1; long snapshotSequence = 0;
        HashSet<string>? population = null;
        var scenario = JsonDefaults.ReadScenario(Path.Combine(directory, "scenario.json"));
        var definitions = CFIL.Domain.PopulationFactory.Create(scenario).ToDictionary(v => v.Id);
        var replayVehicles = definitions.ToDictionary(v => v.Key, v => new CFIL.Domain.Vehicle(v.Value));
        using var replayEvents = File.ReadLines(Path.Combine(directory, "events.jsonl")).GetEnumerator();
        long replaySequence = 0;
        foreach (var line in File.ReadLines(Path.Combine(directory, "snapshots.jsonl")))
        {
            var s = JsonDefaults.Deserialize<SimulationSnapshot>(line);
            if (s.SchemaVersion != "1.0" || s.RunId != runId || s.SimulationTimeMs < snapshotTime || s.SimulationTimeMs > time ||
                s.LastEventSequence < snapshotSequence || s.LastEventSequence > sequence || s.LastEventSequence < 1 ||
                eventTimes[(int)s.LastEventSequence] > s.SimulationTimeMs)
                throw new InvalidDataException("Snapshot is inconsistent with event log.");
            while (replaySequence < s.LastEventSequence)
            {
                if (!replayEvents.MoveNext()) throw new InvalidDataException("Truncated event replay.");
                var e = JsonDefaults.Deserialize<SimulationEvent>(replayEvents.Current); replaySequence = e.Sequence;
                if (!replayVehicles.TryGetValue(e.EntityId, out var vehicle)) continue;
                CFIL.Domain.VehicleStatus? next = e.EventType switch
                {
                    "VehicleEnteredHighway" => CFIL.Domain.VehicleStatus.Driving,
                    "QueueEntered" => CFIL.Domain.VehicleStatus.Queued,
                    "ChargingStarted" => CFIL.Domain.VehicleStatus.Charging,
                    "VehicleDepartedPark" => CFIL.Domain.VehicleStatus.Driving,
                    "VehicleReachedDestination" => CFIL.Domain.VehicleStatus.Arrived,
                    "VehicleStranded" => CFIL.Domain.VehicleStatus.Stranded,
                    "VehicleStateObserved" => Enum.Parse<CFIL.Domain.VehicleStatus>(((JsonElement)e.Payload).GetProperty("state").GetString()!),
                    _ => null
                };
                if (next is not null)
                {
                    try { vehicle.Transition(next.Value); }
                    catch (InvalidOperationException ex) { throw new InvalidDataException("Event replay has an illegal state transition.", ex); }
                }
            }
            var ids = s.Vehicles.Select(v => v.Id).ToHashSet(); population ??= ids;
            if (ids.Count != s.Vehicles.Length || !population.SetEquals(ids) || !ids.SetEquals(definitions.Keys)) throw new InvalidDataException("Invalid snapshot population.");
            foreach (var v in s.Vehicles)
            {
                if (!double.IsFinite(v.EnergyKwh) || !double.IsFinite(v.CapacityKwh) || v.CapacityKwh <= 0 || v.EnergyKwh < 0 ||
                    v.EnergyKwh > v.CapacityKwh || !Enum.TryParse<CFIL.Domain.VehicleStatus>(v.Status, out _) || v.DistanceM < 0)
                    throw new InvalidDataException("Invalid snapshot vehicle state.");
                var d = definitions[v.Id];
                if (v.Status != replayVehicles[v.Id].Status.ToString() || v.CapacityKwh != d.CapacityKwh ||
                    Math.Abs(d.InitialSoc * d.CapacityKwh + v.ChargedKwh - v.ConsumedKwh - v.EnergyKwh) > 1e-6)
                    throw new InvalidDataException("Snapshot contradicts replayed state or energy conservation.");
            }
            var occupied = new HashSet<string>();
            foreach (var p in s.Parks)
                foreach (var (bay, id) in p.Occupants)
                    if (!ids.Contains(id) || !occupied.Add(id) || !s.Vehicles.Any(v => v.Id == id && v.BayId == bay && v.ParkId == p.Id))
                        throw new InvalidDataException("Invalid snapshot bay occupancy.");
            snapshotTime = s.SimulationTimeMs; snapshotSequence = s.LastEventSequence; count++;
        }
        if (count == 0 || snapshotSequence != sequence || snapshotTime != time) throw new InvalidDataException("Missing final snapshot.");
        if (decisionTimes.Count != outcomes.Count) throw new InvalidDataException("Missing observed or censored decision outcome.");
        return new(runId!, sequence, time, count, decisionTimes.Count, outcomes.Count,
            HashFile(Path.Combine(directory, "events.jsonl")), HashFile(Path.Combine(directory, "snapshots.jsonl")));
    }
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    public static void Compare(string left, string right)
    {
        using var manifestA = JsonDocument.Parse(File.ReadAllText(Path.Combine(left, "manifest.json")));
        using var manifestB = JsonDocument.Parse(File.ReadAllText(Path.Combine(right, "manifest.json")));
        foreach (var property in new[] { "scenarioHash", "modelVersion", "sourceHash", "traffic", "runtime", "environment", "processArchitecture" })
            if (manifestA.RootElement.GetProperty(property).GetRawText() != manifestB.RootElement.GetProperty(property).GetRawText())
                throw new InvalidDataException("Replay environment or configuration mismatch: " + property);
        var a = Validate(left); var b = Validate(right);
        if (a.EventHash != b.EventHash || a.SnapshotHash != b.SnapshotHash) throw new InvalidDataException("Deterministic replay mismatch.");
        using var one = JsonDocument.Parse(File.ReadAllText(Path.Combine(left, "metrics.json")));
        using var two = JsonDocument.Parse(File.ReadAllText(Path.Combine(right, "metrics.json")));
        // Wall-clock performance belongs to the environment, not deterministic business outcomes.
        string Normalize(JsonElement e) => JsonSerializer.Serialize(e.EnumerateObject().Where(p => p.Name != "performance")
            .ToDictionary(p => p.Name, p => p.Value));
        if (Normalize(one.RootElement) != Normalize(two.RootElement)) throw new InvalidDataException("KPI replay mismatch.");
    }
}
