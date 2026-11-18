using System.Text.Json;

namespace CFIL.Simulation;

/// <summary>Language boundary for CFIL-02. This contract does not implement a learner.</summary>
public static class LearningContract
{
    public const string Target = "QUEUE_TO_CHARGE_START_SECONDS";
    public const string Version = "1.0";
    private static readonly string[] Common = ["schemaVersion", "messageId", "messageType", "runId", "observationId",
        "chargingAttemptId", "vehicleId", "target", "targetVersion", "featureSchemaVersion", "occurredAtSimTimeMs",
        "availableAtSimTimeMs", "sourceCutoffSequence"];
    private static readonly string[] Features = ["vehicleKind", "soc", "batteryCapacityKwh", "maxGridChargingKw", "vehiclesAhead",
        "occupiedCompatibleBays", "compatibleBayCount", "blockedCompatibleBays", "availableGridKw", "arrivalsLast900Seconds",
        "completedChargeCount", "meanCompletedChargeSeconds", "timeOfDaySeconds"];

    public static void Validate(JsonElement message)
    {
        static void Check(bool condition) { if (!condition) throw new InvalidDataException("Invalid CFIL learning contract."); }
        static bool Number(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var n) && double.IsFinite(n) && n >= 0;
        static bool Fields(JsonElement e, IEnumerable<string> names) => e.ValueKind == JsonValueKind.Object &&
            e.EnumerateObject().Count() == names.Count() && e.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(names);
        try
        {
            var kind = message.GetProperty("messageType").GetString();
            Check(kind is "ObservationOpened" or "OutcomeResolved");
            var opened = kind == "ObservationOpened";
            Check(Fields(message, Common.Concat(opened ? ["features", "featureHash"] : ["status", "targetSeconds", "lowerBoundSeconds", "reason"])));
            Check(message.GetProperty("schemaVersion").GetString() == Version && message.GetProperty("targetVersion").GetString() == Version &&
                message.GetProperty("featureSchemaVersion").GetString() == Version && message.GetProperty("target").GetString() == Target);
            foreach (var field in new[] { "messageId", "runId", "observationId", "chargingAttemptId", "vehicleId" })
                Check(!string.IsNullOrWhiteSpace(message.GetProperty(field).GetString()));
            var id = message.GetProperty("observationId").GetString();
            Check(id == message.GetProperty("chargingAttemptId").GetString() + ":queue-v1");
            Check(message.GetProperty("messageId").GetString() == id + (opened ? ":opened" : ":resolved"));
            Check(message.GetProperty("sourceCutoffSequence").GetInt64() > 0);
            var time = message.GetProperty("occurredAtSimTimeMs").GetInt64();
            Check(time >= 0 && message.GetProperty("availableAtSimTimeMs").GetInt64() == time);
            if (opened)
            {
                var f = message.GetProperty("features"); Check(Fields(f, Features));
                Check(f.GetProperty("vehicleKind").GetString() is "Car" or "Truck");
                foreach (var name in Features.Except(["vehicleKind", "soc", "meanCompletedChargeSeconds"])) Check(Number(f.GetProperty(name)));
                var soc = f.GetProperty("soc"); Check(soc.ValueKind == JsonValueKind.Null || Number(soc) && soc.GetDouble() <= 1);
                var mean = f.GetProperty("meanCompletedChargeSeconds");
                Check(mean.ValueKind == JsonValueKind.Null || Number(mean));
                Check((f.GetProperty("completedChargeCount").GetInt32() == 0) == (mean.ValueKind == JsonValueKind.Null));
                foreach (var name in new[] { "vehiclesAhead", "occupiedCompatibleBays", "compatibleBayCount", "blockedCompatibleBays", "arrivalsLast900Seconds", "completedChargeCount" })
                    Check(f.GetProperty(name).TryGetInt32(out _));
                Check(f.GetProperty("batteryCapacityKwh").GetDouble() > 0 && f.GetProperty("maxGridChargingKw").GetDouble() > 0 && f.GetProperty("timeOfDaySeconds").GetDouble() < 86400);
                var hash = message.GetProperty("featureHash").GetString();
                Check(hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
                // Python owns the versioned canonical hash encoding. Consumers must verify it;
                // this .NET boundary checks its representation without inventing a second encoder.
            }
            else
            {
                var status = message.GetProperty("status").GetString();
                Check(status is "OBSERVED" or "RIGHT_CENSORED" or "COMPETING_EVENT" or "INCOMPLETE_RUN");
                Check(!string.IsNullOrWhiteSpace(message.GetProperty("reason").GetString()));
                var target = message.GetProperty("targetSeconds"); var lower = message.GetProperty("lowerBoundSeconds");
                Check(status == "OBSERVED" ? Number(target) && lower.ValueKind == JsonValueKind.Null : target.ValueKind == JsonValueKind.Null && Number(lower));
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Malformed CFIL learning contract.", ex); }
    }
}
