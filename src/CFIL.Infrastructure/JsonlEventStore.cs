using System.Text;
using CFIL.Simulation;

namespace CFIL.Infrastructure;

public sealed class JsonlEventStore : ISimulationEventSink, IAsyncDisposable
{
    private readonly StreamWriter events;
    private readonly StreamWriter telemetry;
    private readonly StreamWriter snapshots;
    private long lastSequence;
    private long lastTime;
    private long flushedTime = -60000;
    private readonly string progressPath;
    private long progressPublishFailures;
    public JsonlEventStore(string directory)
    {
        Directory.CreateDirectory(directory);
        progressPath = Path.Combine(directory, "producer-progress.json");
        static StreamWriter Create(string path) => new(new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 65536, FileOptions.SequentialScan), new UTF8Encoding(false), 65536);
        events = Create(Path.Combine(directory, "events.jsonl"));
        telemetry = Create(Path.Combine(directory, "telemetry.jsonl"));
        snapshots = Create(Path.Combine(directory, "snapshots.jsonl"));
    }
    public async Task AppendAsync(SimulationEvent e, CancellationToken ct)
    {
        if (e.Sequence != lastSequence + 1 || e.SimulationTimeMs < lastTime) throw new InvalidDataException("Invalid append order.");
        await events.WriteLineAsync(JsonDefaults.Serialize(e).AsMemory(), ct);
        lastSequence = e.Sequence; lastTime = e.SimulationTimeMs;
        // Bounded visibility even during long periods without snapshots. Diagnostics live in a
        // sidecar so wall-clock timestamps cannot change deterministic simulation event hashes.
        if (lastTime - flushedTime >= 60000 || e.EventType is "SimulationCompleted" or "SimulationFailed")
            await FlushEventsAsync(ct);
    }
    public Task SnapshotAsync(SimulationSnapshot snapshot, CancellationToken ct) => snapshots.WriteLineAsync(JsonDefaults.Serialize(snapshot).AsMemory(), ct);
    public Task TelemetryAsync(long timeMs, IReadOnlyList<VehicleSnapshot> vehicles, CancellationToken ct) =>
        telemetry.WriteLineAsync(JsonDefaults.Serialize(new { schemaVersion = "1.0", simulationTimeMs = timeMs, vehicles }).AsMemory(), ct);
    public async Task FlushAsync(CancellationToken ct)
    {
        await FlushEventsAsync(ct); await telemetry.FlushAsync(ct); await snapshots.FlushAsync(ct);
    }
    private async Task FlushEventsAsync(CancellationToken ct)
    {
        await events.FlushAsync(ct);
        try
        {
            await JsonDefaults.WriteAsync(progressPath, new { schemaVersion = "1.0", sequence = lastSequence,
                simulationTimeMs = lastTime, byteOffset = events.BaseStream.Position, flushedAtUtc = DateTimeOffset.UtcNow,
                progressPublishFailures }, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A status reader without Windows FILE_SHARE_DELETE can briefly prevent rename.
            // Monitoring is best effort and must never alter the simulation's decisions or
            // interrupt an already appended event. The next successful watermark reports failures.
            progressPublishFailures++;
        }
        flushedTime = lastTime;
    }
    public async ValueTask DisposeAsync() { await events.DisposeAsync(); await telemetry.DisposeAsync(); await snapshots.DisposeAsync(); }
}
