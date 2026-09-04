using Lakehouse.Api.Auth;
using Lakehouse.Application.Lineage;
using Lakehouse.Domain.Lineage;

namespace Lakehouse.Api.Endpoints;

/// <summary>
/// Column-level lineage: the Mermaid graph, the full column list, transitive provenance (upstream) and
/// impact analysis (downstream). The graph is derived from the declared transforms, so it is always
/// consistent with the pipeline code.
/// </summary>
public static class LineageEndpoints
{
    private static readonly Lazy<LineageGraph> Graph = new(LineageCatalog.Build);

    public static IEndpointRouteBuilder MapLineageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/lineage").WithTags("Lineage").RequireAuthorization(AuthPolicies.Reader);

        group.MapGet("/mermaid", () => Results.Text(Graph.Value.ToMermaid(), "text/plain"));

        group.MapGet("/columns", () =>
            Results.Ok(Graph.Value.AllColumns().Select(c => c.ToString()).OrderBy(s => s, StringComparer.Ordinal)));

        group.MapGet("/upstream", (string column) => Resolve(column, c => Graph.Value.Upstream(c)));

        group.MapGet("/impact", (string column) => Resolve(column, c => Graph.Value.Impact(c)));

        return app;
    }

    private static IResult Resolve(string column, Func<ColumnRef, IReadOnlyList<ColumnRef>> query)
    {
        if (string.IsNullOrWhiteSpace(column))
            return Results.BadRequest(new { error = "Query parameter 'column' (dataset.column) is required." });
        try
        {
            var target = ColumnRef.Parse(column);
            var result = query(target).Select(c => c.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToList();
            return Results.Ok(new { column = target.ToString(), count = result.Count, columns = result });
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
}
