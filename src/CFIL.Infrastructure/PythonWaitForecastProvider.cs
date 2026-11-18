using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CFIL.Simulation;

namespace CFIL.Infrastructure;

public sealed class DecisionTrace : IDecisionTrace
{
    private readonly FileStream file;
    private readonly StreamWriter writer;
    private long sequence;
    public DecisionTrace(string path)
    {
        file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        writer = new(file, new UTF8Encoding(false), 65536, leaveOpen: true);
    }
    public async Task AppendAsync(string type, object payload, CancellationToken ct)
    {
        await writer.WriteLineAsync(JsonDefaults.Serialize(new { sequence = ++sequence, type, payload }).AsMemory(), ct);
        await writer.FlushAsync(ct);
        file.Flush(flushToDisk: true);
    }
    public async ValueTask DisposeAsync() { await writer.DisposeAsync(); await file.DisposeAsync(); }
}

public sealed class PythonWaitForecastProvider(string python, string source, string experiment, PlanningOptions options,
    string output) : IWaitForecastProvider
{
    private Process? process;
    private Task? drain;
    private StreamWriter? errors;
    private readonly List<RealizedDecision> pending = [];
    private readonly DecisionTrace protocol = new(Path.Combine(output, "provider-protocol.jsonl"));
    private bool injected;

    private async Task EnsureRunning(CancellationToken ct)
    {
        if (process is { HasExited: false }) return;
        await StopAsync();
        var start = new ProcessStartInfo(python) { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-u"); start.ArgumentList.Add(Path.Combine(RunExecutor.FindRoot(), "scripts/prearrival_provider.py"));
        process = Process.Start(start) ?? throw new IOException("Cannot start forecast provider.");
        errors ??= new StreamWriter(Path.Combine(output, "forecast.stderr.log"), append: true) { AutoFlush = true };
        var child = process;
        drain = Task.Run(async () => { while (await child.StandardError.ReadLineAsync() is { } line) await errors.WriteLineAsync(line); });
        await Request(new { operation = "initialize", source = Path.GetFullPath(source), directory = Path.GetFullPath(options.StateDirectory),
            experiment, policy = options.Policy.ToString(), development = options.DevelopmentTraining, initialModel = options.InitialModel,
            learningStrategy = options.LearningStrategy }, ct, 30);
    }

    private async Task<JsonElement> Request(object request, CancellationToken ct, double? seconds = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds ?? options.ForecastTimeoutSeconds));
        var timer = Stopwatch.StartNew();
        try
        {
            if (process is null || process.HasExited) throw new IOException("Forecast process unavailable.");
            await process.StandardInput.WriteLineAsync(JsonDefaults.Serialize(request).AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("Forecast protocol closed.");
            using var reply = JsonDocument.Parse(line);
            if (!reply.RootElement.GetProperty("ok").GetBoolean()) throw new IOException(reply.RootElement.GetProperty("error").GetString());
            var result = reply.RootElement.GetProperty("result").Clone();
            await protocol.AppendAsync("Rpc", new { request, result, wallSeconds = timer.Elapsed.TotalSeconds }, ct);
            return result;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException)
        {
            // Discard a timed-out pipe: a late reply must never be consumed as the
            // answer to a newer decision. SQLite recovers before any next request.
            await StopAsync();
            await protocol.AppendAsync("RpcFailure", new { request, error = ex.GetType().Name, wallSeconds = timer.Elapsed.TotalSeconds }, CancellationToken.None);
            if (ct.IsCancellationRequested) throw;
            if (ex is OperationCanceledException) throw new TimeoutException("Forecast deadline exceeded; deterministic fallback required.", ex);
            throw new IOException("Forecast request failed: " + ex.Message, ex);
        }
    }

    private async Task FlushCommits(CancellationToken ct)
    {
        await EnsureRunning(ct);
        // RealizedDecision was flushed to the decision trace before being queued.
        // Retries retain its ORIGINAL features/prediction; they never re-predict.
        // Deliver every pending commitment before ingesting more source outcomes.
        while (pending.Count > 0)
        {
            await Request(new { operation = "commit", decision = pending[0] }, ct);
            pending.RemoveAt(0);
        }
    }

    public async Task SynchronizeAsync(long sourceSequence, long timeMs, CancellationToken ct)
    {
        try
        {
            await FlushCommits(ct);
            await Request(new { operation = "sync", sourceSequence, timeMs }, ct);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            await protocol.AppendAsync("ProviderUnavailable", new { sourceSequence, timeMs, error = ex.Message }, ct);
        }
    }

    public async Task<ForecastBatch> ForecastAsync(ChargingDecisionContext context, ChargingCandidate[] candidates, CancellationToken ct)
    {
        await FlushCommits(ct);
        if (!injected && options.Fault is "forecast-timeout-once" or "forecast-kill-once")
        {
            injected = true;
            var pid = process!.Id;
            if (options.Fault == "forecast-timeout-once")
            {
                // A test-only unanswered request exercises the real deadline and
                // pipe disposal, not a fabricated successful model response.
                await Request(new { operation = "test-hang", seconds = options.ForecastTimeoutSeconds + 10 }, ct);
            }
            else
            {
                process.Kill(entireProcessTree: true); await process.WaitForExitAsync(ct);
                await protocol.AppendAsync("ExternalProviderKill", new { pid, exitCode = process.ExitCode, context.DecisionId }, ct);
                throw new IOException("Injected external forecast process loss.");
            }
        }
        var reply = await Request(new { operation = "forecast", context, candidates }, ct);
        return JsonDefaults.Deserialize<ForecastBatch>(reply.GetRawText());
    }

    public async Task CommitAsync(RealizedDecision decision, CancellationToken ct)
    {
        if (pending.Count >= 1000) throw new IOException("Forecast commitment backlog exceeds bound.");
        pending.Add(decision);
        try { await FlushCommits(ct); }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            await protocol.AppendAsync("CommitDeferred", new { decision.AttemptId, error = ex.Message }, ct);
        }
    }

    public async Task CompleteAsync(long sourceSequence, long timeMs, CancellationToken ct)
    {
        await FlushCommits(ct);
        await Request(new { operation = "complete", sourceSequence, timeMs }, ct, 120);
    }

    private async Task StopAsync()
    {
        if (process is null) return;
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        if (drain is not null) await drain;
        process.Dispose(); process = null; drain = null;
    }
    public async ValueTask DisposeAsync()
    {
        if (process is { HasExited: false })
        {
            process.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { /* StopAsync terminates a nonresponsive provider. */ }
        }
        await StopAsync();
        if (errors is not null) await errors.DisposeAsync();
        await protocol.DisposeAsync();
    }
}
