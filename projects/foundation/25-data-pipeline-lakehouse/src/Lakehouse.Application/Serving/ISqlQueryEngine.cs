namespace Lakehouse.Application.Serving;

/// <summary>
/// Read-only SQL serving over the gold layer. Implemented in Infrastructure by loading the gold tables
/// into SQLite. The engine enforces read-only access, a row cap and a statement timeout regardless of
/// the query, complementing the up-front <see cref="SqlGuard"/>.
/// </summary>
public interface ISqlQueryEngine
{
    /// <summary>(Re)load the gold serving tables from the lake into the query store.</summary>
    void Rebuild();

    /// <summary>Run a guarded, read-only query, capped at <paramref name="maxRows"/> and <paramref name="timeout"/>.</summary>
    QueryResult Query(string sql, int maxRows = 1000, TimeSpan? timeout = null);

    /// <summary>The tables currently available to query.</summary>
    IReadOnlyList<string> Tables();
}
