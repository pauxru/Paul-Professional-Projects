namespace Lakehouse.Domain.Schemas;

/// <summary>A single column definition: name, logical type and nullability.</summary>
public sealed record ColumnDef(string Name, ColumnType Type, bool Nullable = true);

/// <summary>
/// A versioned, ordered set of columns with an optional business/primary key. Schemas are immutable;
/// evolution produces a new version via <see cref="Evolve"/>. This is the registered schema per
/// version that the table format persists (Delta/Iceberg call this the schema history).
/// </summary>
public sealed class TableSchema
{
    private readonly Dictionary<string, ColumnDef> _byName;

    public int Version { get; }
    public IReadOnlyList<ColumnDef> Columns { get; }
    public IReadOnlyList<string> PrimaryKey { get; }

    public TableSchema(int version, IReadOnlyList<ColumnDef> columns, IReadOnlyList<string>? primaryKey = null)
    {
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version), "Schema version starts at 1.");
        if (columns.Count == 0) throw new ArgumentException("A schema needs at least one column.", nameof(columns));
        var names = columns.Select(c => c.Name).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new ArgumentException("Duplicate column names in schema.", nameof(columns));

        Version = version;
        Columns = columns;
        PrimaryKey = primaryKey ?? Array.Empty<string>();
        _byName = columns.ToDictionary(c => c.Name, StringComparer.Ordinal);

        foreach (var pk in PrimaryKey)
            if (!_byName.ContainsKey(pk))
                throw new ArgumentException($"Primary key column '{pk}' is not in the schema.", nameof(primaryKey));
    }

    public bool Has(string column) => _byName.ContainsKey(column);

    public ColumnDef? Find(string column) => _byName.GetValueOrDefault(column);

    public ColumnDef Require(string column) =>
        _byName.TryGetValue(column, out var c) ? c : throw new KeyNotFoundException($"Column '{column}' not found in schema v{Version}.");

    /// <summary>
    /// Additive schema evolution: append new columns, producing the next version. Removing or retyping a
    /// column is intentionally not supported — that is a breaking change a real platform would reject.
    /// </summary>
    public TableSchema Evolve(params ColumnDef[] newColumns)
    {
        foreach (var c in newColumns)
        {
            if (_byName.ContainsKey(c.Name))
                throw new InvalidOperationException($"Column '{c.Name}' already exists; only additive evolution is allowed.");
            if (!c.Nullable)
                throw new InvalidOperationException($"Added column '{c.Name}' must be nullable so historical rows remain readable.");
        }
        return new TableSchema(Version + 1, Columns.Concat(newColumns).ToList(), PrimaryKey);
    }

    public static TableSchema Of(int version, IReadOnlyList<string>? primaryKey, params ColumnDef[] columns) =>
        new(version, columns, primaryKey);
}
