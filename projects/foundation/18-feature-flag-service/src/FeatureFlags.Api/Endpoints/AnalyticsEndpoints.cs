using FeatureFlags.Application;

namespace FeatureFlags.Api.Endpoints;

public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var analytics = app.MapGroup("/api/v1/analytics").RequireAuthorization("FlagsRead").RequireRateLimiting("api");
        analytics.MapGet("/{projectKey}/{environmentKey}/metrics", async (string projectKey, string environmentKey, FlagService service, HttpContext context) =>
            Results.Ok(await service.GetMetricsAsync(projectKey, environmentKey, context.RequestAborted)));
        analytics.MapGet("/{projectKey}/{environmentKey}/experiments/{flagKey}", async (string projectKey, string environmentKey, string flagKey, FlagService service, HttpContext context) =>
        {
            var metrics = (await service.GetMetricsAsync(projectKey, environmentKey, context.RequestAborted)).Where(item => item.FlagKey == flagKey).ToArray();
            var events = await service.GetAnalyticsEventsAsync(projectKey, environmentKey, context.RequestAborted);
            var conversions = events.Where(item => item.Kind == "conversion" && item.FlagKey == flagKey)
                .GroupBy(item => item.VariationIndex ?? -1).ToDictionary(group => group.Key, group => group.Count());
            return Results.Ok(metrics.Select(metric => new
            {
                metric.VariationIndex,
                evaluations = metric.Count,
                uniqueContexts = metric.UniqueContextCount,
                conversions = conversions.GetValueOrDefault(metric.VariationIndex ?? -1, 0),
                conversionRate = metric.Count == 0 ? 0m : decimal.Round((decimal)conversions.GetValueOrDefault(metric.VariationIndex ?? -1, 0) / metric.Count, 4)
            }));
        });
        return app;
    }
}
