using System.Diagnostics;
using System.Text.Json;
using CFIL.Domain;
using CFIL.Simulation;

namespace CFIL.Infrastructure;

public sealed class SumoTrafficSimulator(string pythonPath, string bridgePath, TimeSpan? timeout = null) : ITrafficSimulator, IPlanningTraffic
{
    private Process? process;
    private StreamWriter? diagnostics;
    private Task? drain;
    private readonly TimeSpan timeout = timeout ?? TimeSpan.FromSeconds(60);
    private readonly Dictionary<string, TrafficObservation> observations = new(StringComparer.Ordinal);
    public int? ProviderProcessId => process?.Id;

    public async Task<TrafficMetadata> InitializeAsync(Scenario scenario, VehicleDefinition[] population, string outputDirectory, CancellationToken ct)
    {
        if (process is not null) throw new InvalidOperationException("Already initialized.");
        var start = new ProcessStartInfo(pythonPath) { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-u"); start.ArgumentList.Add(Path.GetFullPath(bridgePath));
        diagnostics = new StreamWriter(Path.Combine(outputDirectory, "bridge.stderr.log"), false) { AutoFlush = true };
        process = Process.Start(start) ?? throw new IOException("Cannot start SUMO bridge.");
        // stderr must be drained concurrently or a full diagnostics pipe can deadlock the lockstep protocol.
        drain = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line) await diagnostics.WriteLineAsync(line);
        }, CancellationToken.None);
        return await Request<TrafficMetadata>(new { operation = "initialize", scenario, population, outputDirectory = Path.GetFullPath(outputDirectory) }, ct);
    }
    public async Task<TrafficStepResult> StepAsync(SimulationStep step, CancellationToken ct)
    {
        var result = await Request<TrafficStepResult>(new { operation = "step", step.SimulationTimeMs, step.ElapsedSeconds, step.Commands }, ct);
        foreach (var observation in result.Observations) observations[observation.VehicleId] = observation;
        // This is lossless wire compression, not telemetry sampling: Python verifies SUMO's stationary
        // state every second. Confirmed unchanged longitudinal states use an ID instead of nine fields.
        // The simulation still receives a complete observation set and accounts every energy/time interval.
        var restored = result.Observations.Concat((result.UnchangedVehicleIds ?? []).Select(id =>
            observations.TryGetValue(id, out var state) && state.SpeedMps == 0 && !state.Arrived ? state :
                throw new InvalidDataException("Invalid unchanged-vehicle reference: " + id))).ToArray();
        foreach (var id in result.Arrived) observations.Remove(id);
        return result with { Observations = restored, UnchangedVehicleIds = null };
    }

    public Task<RouteCandidate[]> FindChargingRoutesAsync(string vehicleId, string[] parkIds, CancellationToken ct) =>
        Request<RouteCandidate[]>(new { operation = "planning-routes", vehicleId, parkIds }, ct);
    public Task<PlanReceipt> ApplyPlanAsync(PlanApplication application, CancellationToken ct) =>
        Request<PlanReceipt>(new { operation = "apply-plan", application }, ct);

    private async Task<T> Request<T>(object request, CancellationToken ct)
    {
        if (process is null || process.HasExited) throw new IOException("SUMO bridge is not running.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonDefaults.Serialize(request).AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            var line = await process.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new IOException("SUMO bridge closed its protocol stream.");
            using var json = JsonDocument.Parse(line);
            if (!json.RootElement.GetProperty("ok").GetBoolean())
                throw new IOException("SUMO bridge: " + json.RootElement.GetProperty("error").GetString());
            return JsonDefaults.Deserialize<T>(json.RootElement.GetProperty("result").GetRawText());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"SUMO bridge failed to respond within {timeout.TotalSeconds} seconds.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (process is not null)
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(timeoutCts.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
            if (drain is not null) await drain;
            process.Dispose();
        }
        if (diagnostics is not null) await diagnostics.DisposeAsync();
    }
}
