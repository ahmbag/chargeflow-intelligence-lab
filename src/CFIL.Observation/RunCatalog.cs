using System.Security.Cryptography;
using System.Text.Json;

namespace CFIL.Observation;

public sealed record RunSource(string Key,string Name,string Group,string Directory,string? StateDirectory = null,
    string? ResultPath = null,string? QualityPath = null,string? ExpectedManifestHash = null,string? ExpectedFinancialHash = null);
public sealed class RunCatalog
{
    private readonly Dictionary<string,RunSource> sources;
    public RunCatalog(string root,string configuration)
    {
        Root=Path.GetFullPath(root);
        var records=JsonSerializer.Deserialize<RunSource[]>(File.ReadAllText(configuration),J.Options) ?? [];
        sources=records.ToDictionary(r=>r.Key, r=>r with { Directory=Resolve(r.Directory),
            StateDirectory=r.StateDirectory is null?null:Resolve(r.StateDirectory),ResultPath=r.ResultPath is null?null:Resolve(r.ResultPath),
            QualityPath=r.QualityPath is null?null:Resolve(r.QualityPath) },StringComparer.Ordinal);
        if (records.Any(r=>!System.Text.RegularExpressions.Regex.IsMatch(r.Key,"^[a-z0-9-]{1,80}$"))) throw new InvalidDataException("Invalid catalog key.");
    }
    public string Root { get; }
    private string Resolve(string relative)
    {
        var path=Path.GetFullPath(Path.Combine(Root,relative));
        if (!path.StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source outside approved root.");
        if((File.Exists(path)||Directory.Exists(path)) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked source is not allowed.");
        for(var item=new DirectoryInfo(Path.GetDirectoryName(path)!);item is not null && item.FullName.Length>=Root.Length;item=item.Parent)
            if(item.Exists && (item.Attributes&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Linked source directories are not allowed.");
        return path;
    }
    public RunSource ResolveKey(string key) => sources.TryGetValue(key,out var value)?value:throw new KeyNotFoundException("Run is not in the approved catalog.");
    public RunSource[] Sources => sources.Values.ToArray();
    public RunOverview[] List() => Sources.Select(source=>
    {
        try{return Overview(source);}
        catch(Exception error) when(error is JsonException or IOException or InvalidDataException)
        {
            // One damaged archive must not hide every other approved source, and
            // an exception's filesystem details must never become catalog text.
            return new RunOverview(source.Key,source.Name,source.Group,"unavailable",0,"unavailable","unavailable","unavailable","invalid",0,
                "unavailable","unavailable",0,0,"INVALID_SOURCE",[],["Readable manifest and scenario required"]);
        }
    }).ToArray();
    public static string HashFile(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    public static RunOverview Overview(RunSource source)
    {
        var names=new[]{"manifest.json","external-inputs.json","scenario.json","events.jsonl","snapshots.jsonl","telemetry.jsonl","decisions.jsonl","economic-inputs.json","cost-observations.jsonl","cost-accounting.sqlite","economic-report.json","sumo/network.net.xml"};
        var available=names.Where(n=>File.Exists(Path.Combine(source.Directory,n))).ToArray();
        var missing=names.Except(available).ToArray();
        var manifest=available.Contains("manifest.json")?J.Read(Path.Combine(source.Directory,"manifest.json")):J.Empty;
        var scenario=available.Contains("scenario.json")?J.Read(Path.Combine(source.Directory,"scenario.json")):J.Empty;
        var economics=File.Exists(Path.Combine(source.Directory,"economic-config.json"))?J.Read(Path.Combine(source.Directory,"economic-config.json")):J.Empty;
        var status=manifest.Text("status","missing");
        var learningStrategy=manifest.Get("chargingPlanning")?.Text("learningStrategy")??"";
        var integrity=source.ExpectedManifestHash is null?"No accepted checksum supplied":"Accepted manifest verified";
        if(source.ExpectedManifestHash is not null && (!available.Contains("manifest.json") || HashFile(Path.Combine(source.Directory,"manifest.json"))!=source.ExpectedManifestHash)) integrity="CHECKSUM_MISMATCH";
        if(status is "running" or "initializing" && File.Exists(Path.Combine(source.Directory,"producer-progress.json")))
        {
            var progress=J.Read(Path.Combine(source.Directory,"producer-progress.json"));
            if(DateTimeOffset.TryParse(progress.Text("flushedAtUtc"),out var when) && DateTimeOffset.UtcNow-when>TimeSpan.FromSeconds(30)) status="incomplete";
        }
        return new(source.Key,source.Name,source.Group,manifest.Text("runId","unavailable"),(int)manifest.Long("seed"),manifest.Text("scenarioId","unknown"),
            economics.Text("strategy",manifest.Get("chargingPlanning")?.Text("policy",manifest.Text("algorithm","unknown"))??"unknown")+
                (learningStrategy.Length>0?" / "+learningStrategy:""),economics.Text("mode","OFF"),status,
            manifest.Long("requestedSeconds",scenario.Long("durationSeconds"))*1000,manifest.Text("modelVersion","unknown"),manifest.Text("sourceHash","unknown"),
            (int)scenario.Long("truckCount"),(int)scenario.Long("carCount"),integrity,available,missing,source.Group.StartsWith("TEST",StringComparison.Ordinal)?"TEST FIXTURE · EXTRACTED REAL":"SYNTHETIC");
    }
}
