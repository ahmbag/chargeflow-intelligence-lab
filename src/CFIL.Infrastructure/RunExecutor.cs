using System.Runtime.InteropServices;
using System.Text.Json;
using CFIL.Domain;
using CFIL.Simulation;

namespace CFIL.Infrastructure;

public sealed class RunControl
{
    private readonly SemaphoreSlim signal = new(0, 1);
    private volatile bool paused;
    private int steps;
    public void Pause() => paused = true;
    public void Resume() { paused = false; Wake(); }
    public void Step() { paused = true; Interlocked.Increment(ref steps); Wake(); }
    private void Wake() { if (signal.CurrentCount == 0) signal.Release(); }
    public async Task WaitAsync(CancellationToken ct)
    {
        while (paused)
        {
            if (Interlocked.CompareExchange(ref steps, 0, 0) > 0) { Interlocked.Decrement(ref steps); return; }
            await signal.WaitAsync(ct);
        }
    }
}

public static class RunExecutor
{
    public sealed record ExternalInputs(Scenario Scenario, VehicleDefinition[] Population, string ScenarioHash, string PopulationHash);
    public static ExternalInputs Materialize(Scenario scenario)
    {
        scenario.Validate();
        var population = PopulationFactory.Create(scenario);
        return new(scenario, population, JsonDefaults.Hash(JsonDefaults.Serialize(scenario)), JsonDefaults.Hash(JsonDefaults.Serialize(population)));
    }
    public const string ModelVersion = "CFIL-02/1.0.0";
    public static async Task<RunResult> ExecuteAsync(Scenario scenario, string output, ITrafficSimulator traffic,
        CancellationToken ct = default, Action<long>? progress = null, RunControl? control = null,
        PlanningOptions? planning = null, string? pythonPath = null, EconomicOptions? economics = null)
    {
        scenario.Validate();
        planning?.Validate();
        economics?.Validate(planning);
        if (planning is not null && Directory.Exists(planning.StateDirectory) && Directory.EnumerateFileSystemEntries(planning.StateDirectory).Any())
            throw new IOException("Planning state directory is not empty; use a separate experiment.");
        var inputs = Materialize(scenario);
        if (planning?.MaterializedInputs is { } inputPath && JsonDefaults.Serialize(JsonDefaults.Deserialize<ExternalInputs>(File.ReadAllText(inputPath))) != JsonDefaults.Serialize(inputs))
            throw new InvalidDataException("Materialized external inputs differ from the requested population/scenario.");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Output directory is not empty; existing experiment artifacts are never overwritten.");
        Directory.CreateDirectory(output);
        var hash = JsonDefaults.Hash(JsonDefaults.Serialize(scenario));
        var run = new SimulationRun($"{scenario.Id.ToLowerInvariant()}-seed-{scenario.Seed:D4}-{hash[..12]}", hash, ModelVersion, scenario.Seed);
        await JsonDefaults.WriteAsync(Path.Combine(output, "scenario.json"), scenario, ct);
        if (scenario.MultiDay is not null)
            await JsonDefaults.WriteAsync(Path.Combine(output, "journeys.json"), MultiDayPopulation.Identities(inputs.Population), ct);
        if (planning is not null)
        {
            await JsonDefaults.WriteAsync(Path.Combine(output, "external-inputs.json"), inputs, ct);
            await JsonDefaults.WriteAsync(Path.Combine(output, "planning-config.json"), planning, ct);
        }
        TrafficMetadata? metadata = null;
        var status = "initializing";
        string? error = null;
        var sourceHash = SourceHash();
        var startedUtc = DateTimeOffset.UtcNow;
        async Task Manifest(long time) => await JsonDefaults.WriteAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "1.0", run.RunId, scenarioId = scenario.Id, scenario.Seed, scenarioHash = hash,
            modelVersion = ModelVersion,
            algorithm = planning?.Mode == PlanningMode.CONTROLLED ? "planning-" + planning.Policy : scenario.Policy,
            chargingPlanning = planning is null ? null : new { planning.Version, planning.Mode, planning.Policy,
                planning.LearningStrategy,
                featureProfile = ChargingPlanningRules.FeatureVersion,
                initialModelHash = planning.InitialModel is null ? null : ReplayReader.HashFile(planning.InitialModel) },
            runtime = RuntimeInformation.FrameworkDescription,
            environment = RuntimeInformation.OSDescription, processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            traffic = metadata, simulatedSeconds = time / 1000, requestedSeconds = scenario.DurationSeconds, status, error,
            sourceHash, startedUtc
        });
        await using var store = new JsonlEventStore(output);
        await using var economicService = economics is not null && economics.Mode != EconomicMode.OFF
            ? new EconomicService(output,run,economics,inputs.Population,scenario.Parks) : null;
        await using var planner = planning is not null && planning.Mode != PlanningMode.DISABLED
            ? new ChargingPlanner(planning, run, traffic as IPlanningTraffic ?? throw new ArgumentException("Traffic provider has no planning API."),
                new PythonWaitForecastProvider(pythonPath ?? Path.Combine(FindRoot(), ".venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python"),
                    Path.Combine(output, "events.jsonl"), Path.GetFullPath(output), planning, output),
                new DecisionTrace(Path.Combine(output, "decisions.jsonl")),economicService) : null;
        var engine = new SimulationEngine(scenario, run, traffic, store, planner: planner,economics:economicService);
        await Manifest(0);
        await engine.StartAsync(ct);
        try
        {
            metadata = await traffic.InitializeAsync(scenario, PopulationFactory.Create(scenario), output, ct);
            status = "running"; await Manifest(0);
            while (engine.TimeMs < scenario.DurationSeconds * 1000L)
            {
                if (control is not null) await control.WaitAsync(ct);
                await engine.StepAsync(ct);
                if (engine.TimeMs % 3600000 == 0) progress?.Invoke(engine.TimeMs);
            }
            status = "completed";
        }
        catch (Exception ex)
        {
            status = ex is OperationCanceledException ? "cancelled" : "failed"; error = ex.ToString();
        }
        // Even a dead provider or cancellation must preserve the last valid observations and partial metrics.
        var result = await engine.FinishAsync(status, error, CancellationToken.None);
        if (planner is not null)
        {
            try { await planner.CompleteAsync(engine.EventCount, engine.TimeMs, CancellationToken.None); }
            catch (Exception ex) { status = "failed"; error = "Planning finalization: " + ex; }
        }
        await store.DisposeAsync();
        economicService?.Export(output);
        await JsonDefaults.WriteAsync(Path.Combine(output, "metrics.json"), result.Metrics);
        await File.WriteAllLinesAsync(Path.Combine(output, "metrics.csv"), FlattenMetrics(result.Metrics));
        await Manifest(engine.TimeMs);
        ReplayReport? replay = null;
        string? replayError = null;
        try { replay = ReplayReader.Validate(output); } catch (Exception ex) { replayError = ex.Message; }
        await JsonDefaults.WriteAsync(Path.Combine(output, "validation-report.json"), new
        { schemaVersion = "1.0", status, passed = status == "completed" && replayError is null, error, replayError, replay,
            actualTrafficProvider = metadata?.Provider, invariantsCheckedEveryStep = true });
        if (status != "completed" || replayError is not null) throw new InvalidOperationException(error ?? replayError);
        return result;
    }
    private static string SourceHash()
    {
        var root = FindRoot();
        var paths = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "adapters"), "*.py", SearchOption.AllDirectories)).Order(StringComparer.Ordinal);
        return JsonDefaults.Hash(string.Join("\n", paths.Select(p => Path.GetRelativePath(root, p).Replace('\\', '/') + ":" + ReplayReader.HashFile(p))));
    }
    public static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ChargeFlowIntelligenceLab.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }
    private static IEnumerable<string> FlattenMetrics(object metrics)
    {
        yield return "metric,value";
        using var json = JsonDocument.Parse(JsonDefaults.Serialize(metrics));
        foreach (var line in Walk(json.RootElement, "")) yield return line;
        static IEnumerable<string> Walk(JsonElement e, string path)
        {
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject()) foreach (var row in Walk(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name)) yield return row;
            else if (e.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in e.EnumerateArray()) foreach (var row in Walk(item, path + $"[{index++}]")) yield return row;
            }
            else yield return $"\"{path.Replace("\"", "\"\"")}\",\"{e.ToString().Replace("\"", "\"\"")}\"";
        }
    }
}
