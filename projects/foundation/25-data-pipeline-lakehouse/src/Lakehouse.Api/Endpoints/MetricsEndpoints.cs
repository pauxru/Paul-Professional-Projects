using Lakehouse.Api.Auth;
using Lakehouse.Application.Metrics;
using Lakehouse.Application.Serving;

namespace Lakehouse.Api.Endpoints;

/// <summary>
/// The semantic/metrics layer: list named metrics and resolve a metric request ("revenue_usd by channel,
/// monthly") into guarded SQL that is executed against the gold serving store. Callers never write SQL.
/// </summary>
public static class MetricsEndpoints
{
    public sealed record MetricQueryRequest(string Metric, string[]? Dimensions, string? Grain);

    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/metrics").WithTags("Metrics").RequireAuthorization(AuthPolicies.Reader);

        group.MapGet("/catalog", () => Results.Ok(MetricCatalog.All));

        group.MapPost("/query", (MetricQueryRequest body, ISqlQueryEngine engine) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Metric))
                return Results.BadRequest(new { error = "'metric' is required." });

            var grain = TimeGrain.Month;
            if (!string.IsNullOrWhiteSpace(body.Grain) && !Enum.TryParse(body.Grain, ignoreCase: true, out grain))
                return Results.BadRequest(new { error = $"Unknown grain '{body.Grain}'. Use All, Day, Month, Quarter or Year." });

            var query = new MetricQuery(body.Metric, body.Dimensions ?? Array.Empty<string>(), grain);
            try
            {
                var sql = MetricResolver.ToSql(query);
                var result = engine.Query(sql);
                return Results.Ok(new { metric = body.Metric, grain = grain.ToString(), sql, result });
            }
            catch (MetricException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        return app;
    }
}
