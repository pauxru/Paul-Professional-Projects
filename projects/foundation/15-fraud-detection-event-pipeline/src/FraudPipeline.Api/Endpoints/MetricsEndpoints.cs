using FraudPipeline.Application.Feedback;
using FraudPipeline.Application.Scoring;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FraudPipeline.Api.Endpoints;

public static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/metrics").WithTags("Metrics");
        group.MapGet("/detection", DetectionAsync).RequireAuthorization("risk:investigate");
        group.MapGet("/latency", LatencyAsync).RequireAuthorization("risk:investigate");
        return app;
    }

    private static async Task<Ok<DetectionMetrics>> DetectionAsync(DetectionEvaluator evaluator, CancellationToken ct)
    {
        var m = await evaluator.ComputeAsync(2000, ct);
        return TypedResults.Ok(m);
    }

    private static Ok<LatencySummary> LatencyAsync(ScoringMetrics metrics)
        => TypedResults.Ok(metrics.Summarise());
}
