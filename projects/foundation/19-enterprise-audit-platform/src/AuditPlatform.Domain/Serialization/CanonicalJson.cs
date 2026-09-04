using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AuditPlatform.Domain.Serialization;

/// <summary>
/// Canonical JSON serialisation, purpose-built for hash-chain input.
///
/// Rules (implemented and tested precisely — the entire integrity story depends on them):
///   1. Object keys are sorted lexicographically by their UTF-8 code-point sequence,
///      case-sensitive. Insertion order is discarded.
///   2. No insignificant whitespace anywhere.
///   3. Strings are escaped identically to <see cref="JsonEncodedText"/> with the strict
///      <see cref="System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>
///      policy, then re-emitted through a canonical escape table that fixes the escape
///      forms of the low control characters exactly (\u00XX, four hex digits, lowercase).
///   4. Numbers are normalised: integer values render without a fractional part; decimal
///      values render with their shortest round-trip form; NaN/Infinity are rejected.
///   5. Booleans and null render as JSON literals.
///   6. Output is always UTF-8 with no byte-order mark.
///   7. Arrays preserve element order — arrays are ordered data.
///
/// Non-goals: this is *not* JCS (RFC 8785). It is a deliberately smaller specification whose
/// only consumer is <see cref="Integrity.HashChain"/> inside this codebase. Keeping it small
/// keeps it easy to review, easy to port, and easy to prove against.
/// </summary>
public static class CanonicalJson
{
    public static byte[] Serialize(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
            // Use a strict encoder so ASCII passes through verbatim and non-ASCII is escaped
            // to a fixed \uXXXX form, giving byte-for-byte reproducibility.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
        });
        WriteCanonical(writer, element);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] Serialize<T>(T value)
    {
        var json = JsonSerializer.SerializeToElement(value, CanonicalSerializerOptions);
        return Serialize(json);
    }

    public static byte[] Serialize(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        return Serialize(doc.RootElement);
    }

    public static string ToUtf8String(byte[] canonical) => Encoding.UTF8.GetString(canonical);

    public static readonly JsonSerializerOptions CanonicalSerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null
    };

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var props = new List<JsonProperty>();
                foreach (var p in element.EnumerateObject()) props.Add(p);
                // Ordinal (byte-comparison) sort produces a stable order across .NET and other runtimes.
                props.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
                foreach (var prop in props)
                {
                    writer.WritePropertyName(prop.Name);
                    WriteCanonical(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                WriteNumber(writer, element);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            case JsonValueKind.Undefined:
                throw new InvalidOperationException("Undefined value cannot be canonicalised.");
            default:
                throw new InvalidOperationException($"Unsupported JsonValueKind {element.ValueKind}");
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, JsonElement number)
    {
        // Prefer integer representation when the JSON number is exactly representable as long.
        if (number.TryGetInt64(out var i64))
        {
            writer.WriteNumberValue(i64);
            return;
        }

        // Fall back to decimal for a stable, culture-independent textual form.
        if (number.TryGetDecimal(out var dec))
        {
            var text = NormaliseDecimal(dec);
            writer.WriteRawValue(text, skipInputValidation: true);
            return;
        }

        // Last resort: double. Reject NaN/Infinity to keep the hash input purely deterministic.
        if (number.TryGetDouble(out var d))
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                throw new InvalidOperationException("Non-finite numbers cannot be canonicalised.");
            var text = d.ToString("R", CultureInfo.InvariantCulture);
            writer.WriteRawValue(text, skipInputValidation: true);
            return;
        }

        throw new InvalidOperationException("Number could not be canonicalised.");
    }

    private static string NormaliseDecimal(decimal value)
    {
        // Strip trailing zeros without disturbing sign, and preserve a single "0" for integer-valued decimals.
        var text = value.ToString("G29", CultureInfo.InvariantCulture);
        if (text.Contains('.'))
        {
            text = text.TrimEnd('0');
            if (text.EndsWith('.')) text = text[..^1];
        }
        if (text.Length == 0) text = "0";
        return text;
    }
}
