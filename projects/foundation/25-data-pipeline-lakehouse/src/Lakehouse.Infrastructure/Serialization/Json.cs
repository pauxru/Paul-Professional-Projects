using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lakehouse.Infrastructure.Serialization;

/// <summary>Shared System.Text.Json options for the file-backed stores (enums as strings, tolerant reads).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
