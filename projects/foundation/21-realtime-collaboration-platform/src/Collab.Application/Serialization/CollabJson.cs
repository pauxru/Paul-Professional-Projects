using System.Text.Json;

namespace Collab.Application.Serialization;

/// <summary>Shared JSON options for wire payloads and persisted CRDT state (stable, compact).</summary>
public static class CollabJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name}.");
}
