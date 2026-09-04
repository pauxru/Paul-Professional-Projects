using System.Text.Json;
using System.Text.Json.Serialization;

namespace FraudPipeline.Domain.Rules;

/// <summary>
/// Serializes <see cref="RulesetDefinition"/> to/from JSON in a stable manner so
/// that hashing the JSON yields a stable version fingerprint.
/// </summary>
public static class RulesetSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(RulesetDefinition def) => JsonSerializer.Serialize(def, Options);

    public static RulesetDefinition Deserialize(string json)
    {
        var def = JsonSerializer.Deserialize<RulesetDefinition>(json, Options)
            ?? throw new InvalidOperationException("Failed to deserialize ruleset definition.");
        return def;
    }
}
