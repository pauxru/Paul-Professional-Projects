namespace Lakehouse.Application.Serving;

/// <summary>A tabular query result: ordered column names and rows of boxed values, plus a truncation flag.</summary>
public sealed record QueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated,
    double ElapsedMs)
{
    public int RowCount => Rows.Count;
}
