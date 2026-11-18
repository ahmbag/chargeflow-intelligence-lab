using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CFIL.Domain;

namespace CFIL.Infrastructure;

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("Null JSON document.");
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static Scenario ReadScenario(string path)
    {
        var scenario = Deserialize<Scenario>(File.ReadAllText(path)); scenario.Validate(); return scenario;
    }
    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions(Options) { WriteIndented = true }), ct);
        File.Move(temporary, path, true);
    }
}
