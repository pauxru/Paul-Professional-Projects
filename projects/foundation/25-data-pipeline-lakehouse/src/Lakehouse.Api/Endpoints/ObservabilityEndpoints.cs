using Lakehouse.Api.Auth;
using Lakehouse.Application.Observability;
using Lakehouse.Application.Orchestration;

namespace Lakehouse.Api.Endpoints;

/// <summary>Operational observability: per-table freshness gauges and rolled-up run metrics.</summary>
public static class ObservabilityEndpoints
{
    public static IEndpointRouteBuilder MapObservabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/observability").WithTags("Observability").RequireAuthorization(AuthPolicies.Reader);

        group.MapGet("/freshness", (FreshnessService freshness) => Results.Ok(freshness.Snapshot()));

        group.MapGet("/metrics", (IRunHistoryStore history) =>
        {
            var runs = history.Recent(100);
            var total = runs.Count;
            var succeeded = runs.Count(r => r.Success);
            var failed = total - succeeded;
            return Results.Ok(new
            {
                totalRuns = total,
                succeeded,
                failed,
                successRate = total == 0 ? 0 : Math.Round((double)succeeded / total, 4),
                failureRate = total == 0 ? 0 : Math.Round((double)failed / total, 4),
                avgDurationMs = total == 0 ? 0 : Math.Round(runs.Average(r => r.DurationMs), 1),
                lastRun = runs.OrderByDescending(r => r.StartedAt).Select(r => new
                {
                    r.RunId, r.Window, r.Success, r.StartedAt, r.FinishedAt, r.DurationMs, r.TotalRowsOut, r.Failed, r.Blocked
                }).FirstOrDefault()
            });
        });

        return app;
    }
}
