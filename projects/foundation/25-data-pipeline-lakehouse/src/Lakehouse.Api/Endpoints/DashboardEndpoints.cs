using Lakehouse.Application.Model;
using Lakehouse.Application.Quality;
using Lakehouse.Application.Serving;

namespace Lakehouse.Api.Endpoints;

/// <summary>
/// Read-only aggregate feeds for the built-in HTML dashboard: revenue trend, cohort-retention heat table,
/// funnel and data-quality status. These serve only gold aggregates (no PII) and are intentionally
/// anonymous so the static dashboard renders without a token; sensitive control-plane routes stay gated.
/// </summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard").WithTags("Dashboard");

        group.MapGet("/revenue-trend", (ISqlQueryEngine engine) => SafeQuery(engine,
            $"SELECT date_key, orders, units, revenue_usd FROM {Tables.AggDailyRevenue} ORDER BY date_key"));

        group.MapGet("/cohort-retention", (ISqlQueryEngine engine) => SafeQuery(engine,
            $"SELECT cohort_month, activity_month, months_since, customers FROM {Tables.AggCohortRetention} ORDER BY cohort_month, months_since"));

        group.MapGet("/funnel", (ISqlQueryEngine engine) => SafeQuery(engine,
            $"SELECT step_order, step, sessions FROM {Tables.AggFunnel} ORDER BY step_order"));

        group.MapGet("/quality", (IDataQualityStore store) =>
        {
            object Summarize(string gate)
            {
                var r = store.Latest(gate);
                return r is null
                    ? new { gate, available = false, passed = 0, failed = 0, blocking = false, evaluatedAt = (DateTimeOffset?)null }
                    : new
                    {
                        gate,
                        available = true,
                        passed = r.Passed,
                        failed = r.Failed,
                        blocking = r.HasBlockingFailure,
                        evaluatedAt = (DateTimeOffset?)r.EvaluatedAt
                    };
            }
            return Results.Ok(new { silver = Summarize("silver"), gold = Summarize("gold") });
        });

        return app;
    }

    private static IResult SafeQuery(ISqlQueryEngine engine, string sql)
    {
        try { return Results.Ok(engine.Query(sql, maxRows: 5000)); }
        catch (Exception) { return Results.Ok(new QueryResult(Array.Empty<string>(), Array.Empty<IReadOnlyList<object?>>(), false, 0)); }
    }
}
