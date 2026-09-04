using System.Text.Json;

namespace ExampleBank.Ledger.Application.Common;

/// <summary>Shared JSON options so idempotency replay serialises/deserialises identically.</summary>
public static class LedgerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
}
