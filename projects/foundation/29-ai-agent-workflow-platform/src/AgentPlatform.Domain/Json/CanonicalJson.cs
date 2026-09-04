using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentPlatform.Domain.Json;

/// <summary>
/// Produces a canonical JSON string (object keys sorted, insignificant whitespace removed) so that
/// two semantically-identical argument objects hash identically. Used for idempotency keys and for
/// loop/oscillation detection.
/// </summary>
public static class CanonicalJson
{
    public static string Serialize(JsonNode? node)
    {
        if (node is null) return "null";
        var canonical = Canonicalize(node);
        return canonical is null ? "null" : canonical.ToJsonString(Options);
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => CanonicalizeObject(obj),
        JsonArray arr => CanonicalizeArray(arr),
        _ => node?.DeepClone(),
    };

    private static JsonObject CanonicalizeObject(JsonObject obj)
    {
        var result = new JsonObject();
        foreach (var key in obj.Select(k => k.Key).OrderBy(k => k, StringComparer.Ordinal))
            result[key] = Canonicalize(obj[key]);
        return result;
    }

    private static JsonArray CanonicalizeArray(JsonArray arr)
    {
        var result = new JsonArray();
        foreach (var item in arr) result.Add(Canonicalize(item));
        return result;
    }
}
