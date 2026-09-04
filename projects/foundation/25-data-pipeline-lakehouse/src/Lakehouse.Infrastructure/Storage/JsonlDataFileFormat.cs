using System.Globalization;
using System.Text;
using System.Text.Json;
using Lakehouse.Domain.Data;
using Lakehouse.Domain.Schemas;

namespace Lakehouse.Infrastructure.Storage;

/// <summary>
/// Newline-delimited JSON encoding. Each line is one row; null/absent columns are simply omitted, which
/// is what makes additive schema evolution free on the read path. Values are written type-aware
/// (decimals and timestamps as strings) so precision and timezone offset survive the round trip.
/// </summary>
public sealed class JsonlDataFileFormat : IDataFileFormat
{
    public string Extension => ".jsonl";

    public void Write(string absolutePath, IReadOnlyList<Row> rows, TableSchema schema)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var col in schema.Columns)
            {
                if (row.IsNull(col.Name)) continue;
                obj[col.Name] = Encode(col.Type, row, col.Name);
            }
            sb.Append(JsonSerializer.Serialize(obj));
            sb.Append('\n');
        }
        File.WriteAllText(absolutePath, sb.ToString(), new UTF8Encoding(false));
    }

    public IReadOnlyList<Row> Read(string absolutePath, TableSchema targetSchema)
    {
        var rows = new List<Row>();
        foreach (var line in File.ReadLines(absolutePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var row = new Row();
            foreach (var col in targetSchema.Columns)
            {
                if (!root.TryGetProperty(col.Name, out var el) || el.ValueKind == JsonValueKind.Null)
                    continue;
                row[col.Name] = Decode(col.Type, el);
            }
            rows.Add(row);
        }
        return rows;
    }

    private static object? Encode(ColumnType type, Row row, string col) => type switch
    {
        ColumnType.String => row.GetString(col),
        ColumnType.Long => row.GetLong(col),
        ColumnType.Double => row.GetDouble(col),
        ColumnType.Decimal => row.GetDecimal(col)?.ToString(CultureInfo.InvariantCulture),
        ColumnType.Bool => row.GetBool(col),
        ColumnType.Timestamp => row.GetTimestamp(col)?.ToString("O", CultureInfo.InvariantCulture),
        _ => row.GetString(col)
    };

    private static object? Decode(ColumnType type, JsonElement el) => type switch
    {
        ColumnType.String => el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString(),
        ColumnType.Long => el.ValueKind == JsonValueKind.Number ? el.GetInt64() : long.Parse(el.GetString()!, CultureInfo.InvariantCulture),
        ColumnType.Double => el.ValueKind == JsonValueKind.Number ? el.GetDouble() : double.Parse(el.GetString()!, CultureInfo.InvariantCulture),
        ColumnType.Decimal => el.ValueKind == JsonValueKind.String ? decimal.Parse(el.GetString()!, CultureInfo.InvariantCulture) : el.GetDecimal(),
        ColumnType.Bool => el.ValueKind is JsonValueKind.True or JsonValueKind.False ? el.GetBoolean()
            : el.ValueKind == JsonValueKind.Number ? el.GetInt64() != 0
            : bool.Parse(el.GetString()!),
        ColumnType.Timestamp => DateTimeOffset.Parse(el.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        _ => el.ToString()
    };
}
