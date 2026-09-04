using Lakehouse.Api.Auth;
using Lakehouse.Application.Quality;

namespace Lakehouse.Api.Endpoints;

/// <summary>Read access to data-quality reports: the latest per gate and recent history.</summary>
public static class QualityEndpoints
{
    public static IEndpointRouteBuilder MapQualityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/quality").WithTags("Quality").RequireAuthorization(AuthPolicies.Reader);

        group.MapGet("/latest", (IDataQualityStore store, string? gate) =>
        {
            var g = string.IsNullOrWhiteSpace(gate) ? "silver" : gate!.Trim().ToLowerInvariant();
            var report = store.Latest(g);
            return report is null ? Results.NotFound(new { error = $"No report for gate '{g}'." }) : Results.Ok(report);
        });

        group.MapGet("/reports", (IDataQualityStore store, int? limit) =>
            Results.Ok(store.Recent(limit is > 0 and <= 200 ? limit.Value : 20)));

        return app;
    }
}
