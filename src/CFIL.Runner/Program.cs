using CFIL.Infrastructure;
using CFIL.Simulation;

try
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("CFIL run <scenario.json> --output <directory> [--seed N] [--duration N] [--python executable] [--planning config.json] [--economics config.json] [--interactive]\nCFIL materialize <scenario.json> --output <inputs.json> [--seed N]\nCFIL replay <directory>\nCFIL decision-replay <directory>\nCFIL compare <first-directory> <second-directory>\nCFIL cost-replay <source-directory> [--database file.sqlite] [--output directory]\nCFIL economic-compare <frozen-experiment-root> <output.json>");
        return 0;
    }
    if (args[0] == "replay" && args.Length == 2) { Console.WriteLine(JsonDefaults.Serialize(ReplayReader.Validate(args[1]))); return 0; }
    if (args[0] == "decision-replay" && args.Length == 2) { Console.WriteLine(JsonDefaults.Serialize(PlanningReplay.Validate(args[1]))); return 0; }
    if (args[0] == "compare" && args.Length == 3) { ReplayReader.Compare(args[1], args[2]); Console.WriteLine("Event, snapshot and KPI replay match."); return 0; }
    if (args[0] == "economic-compare" && args.Length == 3)
    {
        EconomicJson.Write(args[2], EconomicComparison.Evaluate(args[1]));
        Console.WriteLine("Paired economic evaluation completed."); return 0;
    }
    if (args[0] == "cost-replay" && args.Length >= 2)
    {
        var financial = new Dictionary<string,string>();
        for (var i=2;i<args.Length;i+=2)
            if (i+1>=args.Length || args[i] is not ("--database" or "--output" or "--fault" or "--fault-sequence" or "--marker") ||
                !financial.TryAdd(args[i],args[i+1])) throw new ArgumentException("Invalid cost-replay arguments.");
        var output=financial.GetValueOrDefault("--output",args[1]); Directory.CreateDirectory(output);
        CostJournal.Replay(Path.Combine(args[1],"cost-observations.jsonl"),financial.GetValueOrDefault("--database",Path.Combine(output,"cost-replay.sqlite")),output,
            financial.GetValueOrDefault("--fault"),long.Parse(financial.GetValueOrDefault("--fault-sequence","0"),System.Globalization.CultureInfo.InvariantCulture),financial.GetValueOrDefault("--marker"));
        Console.WriteLine("Financial source replay and export completed."); return 0;
    }
    if (args[0] is not ("run" or "materialize") || args.Length < 4) throw new ArgumentException("Use --help for usage.");
    var options = new Dictionary<string, string>(); var interactive = false;
    for (var i = 2; i < args.Length; i++)
    {
        if (args[i] == "--interactive") { interactive = true; continue; }
        if (args[i] is not ("--output" or "--seed" or "--duration" or "--python" or "--planning" or "--economics") || i + 1 == args.Length || !options.TryAdd(args[i], args[++i]))
            throw new ArgumentException("Unknown, duplicate or incomplete CLI option.");
    }
    var scenario = JsonDefaults.ReadScenario(args[1]);
    if (options.TryGetValue("--seed", out var seed)) scenario = scenario with { Seed = int.Parse(seed, System.Globalization.CultureInfo.InvariantCulture) };
    if (options.TryGetValue("--duration", out var duration)) scenario = scenario with { DurationSeconds = int.Parse(duration, System.Globalization.CultureInfo.InvariantCulture) };
    if (args[0] == "materialize")
    {
        if (File.Exists(options["--output"])) throw new IOException("Materialized inputs already exist.");
        await JsonDefaults.WriteAsync(options["--output"], RunExecutor.Materialize(scenario));
        return 0;
    }
    var root = RunExecutor.FindRoot();
    var python = options.GetValueOrDefault("--python", Path.Combine(root, ".venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python"));
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    var control = new RunControl();
    if (interactive)
    {
        control.Pause(); Console.Error.WriteLine("Paused. Commands: r=run, p=pause, s=one step, q=cancel.");
        _ = Task.Run(() =>
        {
            while (!cancellation.IsCancellationRequested && Console.ReadLine() is { } input)
                switch (input.Trim()) { case "r": control.Resume(); break; case "p": control.Pause(); break; case "s": control.Step(); break; case "q": cancellation.Cancel(); break; }
        });
    }
    await using var traffic = new SumoTrafficSimulator(python, Path.Combine(root, "adapters/sumo-bridge/bridge.py"));
    var result = await RunExecutor.ExecuteAsync(scenario, options["--output"], traffic, cancellation.Token,
        t => Console.Error.WriteLine($"Simulated {t / 3600000} h / {scenario.DurationSeconds / 3600d:0.#} h"), control,
        options.TryGetValue("--planning", out var planningPath) ? JsonDefaults.Deserialize<PlanningOptions>(File.ReadAllText(planningPath)) : null, python,
        options.TryGetValue("--economics", out var economicPath) ? EconomicJson.Read<EconomicOptions>(File.ReadAllText(economicPath)) : null);
    Console.WriteLine($"Completed: {result.SimulationTimeMs / 1000}s, {result.EventCount} events. {Path.GetFullPath(options["--output"])}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message); return 1;
}
