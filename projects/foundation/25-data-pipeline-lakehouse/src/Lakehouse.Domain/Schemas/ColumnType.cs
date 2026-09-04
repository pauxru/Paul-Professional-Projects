namespace Lakehouse.Domain.Schemas;

/// <summary>
/// The closed set of logical column types the lake understands. Keeping the set small keeps the
/// storage format, the SQLite serving mapping and the type-coercion rules simple and total.
/// </summary>
public enum ColumnType
{
    String,
    Long,
    Double,
    Decimal,
    Bool,
    Timestamp
}

public static class ColumnTypeExtensions
{
    /// <summary>The SQLite column affinity used when a lake column is projected into the serving store.</summary>
    public static string ToSqliteType(this ColumnType type) => type switch
    {
        ColumnType.String => "TEXT",
        ColumnType.Long => "INTEGER",
        ColumnType.Double => "REAL",
        ColumnType.Decimal => "TEXT",     // stored as exact text to preserve precision
        ColumnType.Bool => "INTEGER",
        ColumnType.Timestamp => "TEXT",   // ISO-8601 for lexical ordering
        _ => "TEXT"
    };
}
