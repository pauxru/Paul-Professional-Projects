using System.Globalization;

namespace Lakehouse.Domain.Data;

/// <summary>
/// A schema-flexible record: an ordered map of column name to boxed value. Values are constrained to
/// the CLR types that back <see cref="Lakehouse.Domain.Schemas.ColumnType"/>
/// (string, long, double, decimal, bool, DateTimeOffset, or null). Rows are cheap to clone so
/// transformations can build new rows without mutating their inputs.
/// </summary>
public sealed class Row
{
    private readonly Dictionary<string, object?> _values;

    public Row() => _values = new(StringComparer.Ordinal);

    public Row(IEnumerable<KeyValuePair<string, object?>> values)
        => _values = new(values, StringComparer.Ordinal);

    public object? this[string column]
    {
        get => _values.TryGetValue(column, out var v) ? v : null;
        set => _values[column] = value;
    }

    public IReadOnlyDictionary<string, object?> Values => _values;
    public IReadOnlyCollection<string> Columns => _values.Keys;
    public int Count => _values.Count;
    public bool Has(string column) => _values.ContainsKey(column);
    public bool IsNull(string column) => !_values.TryGetValue(column, out var v) || v is null;

    public Row Clone() => new(_values);

    /// <summary>Build a row from column/value pairs — convenient for transformation outputs.</summary>
    public static Row Of(params (string Column, object? Value)[] cells)
    {
        var row = new Row();
        foreach (var (column, value) in cells) row[column] = value;
        return row;
    }

    public Row With(string column, object? value)
    {
        var clone = Clone();
        clone[column] = value;
        return clone;
    }

    public Row Without(params string[] columns)
    {
        var clone = Clone();
        foreach (var c in columns) clone._values.Remove(c);
        return clone;
    }

    // ---- typed, tolerant accessors -------------------------------------------------------------

    public string? GetString(string column) => this[column]?.ToString();

    public long? GetLong(string column) => this[column] switch
    {
        null => null,
        long l => l,
        int i => i,
        double d => (long)d,
        decimal m => (long)m,
        bool b => b ? 1 : 0,
        string s => long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : null,
        _ => null
    };

    public double? GetDouble(string column) => this[column] switch
    {
        null => null,
        double d => d,
        long l => l,
        int i => i,
        decimal m => (double)m,
        string s => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : null,
        _ => null
    };

    public decimal? GetDecimal(string column) => this[column] switch
    {
        null => null,
        decimal m => m,
        long l => l,
        int i => i,
        double d => (decimal)d,
        string s => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : null,
        _ => null
    };

    public bool? GetBool(string column) => this[column] switch
    {
        null => null,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        string s => bool.TryParse(s, out var r) ? r : (s == "1" ? true : s == "0" ? false : null),
        _ => null
    };

    public DateTimeOffset? GetTimestamp(string column) => this[column] switch
    {
        null => null,
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
        string s => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var r) ? r : null,
        _ => null
    };

    public override string ToString() =>
        "{" + string.Join(", ", _values.Select(kv => $"{kv.Key}={kv.Value ?? "null"}")) + "}";
}
