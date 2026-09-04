using Microsoft.AspNetCore.Http.HttpResults;
using RagAssistant.Api.Contracts;
using RagAssistant.Application.Evaluation;
using RagAssistant.Domain.Documents;

namespace RagAssistant.Api.Endpoints;

public static class EvaluationEndpoints
{
    public static IEndpointRouteBuilder MapEvaluationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/eval").WithTags("Evaluation").RequireAuthorization("KnowledgeAdmin");
        group.MapPost("/runs", RunAsync).WithName("RunEvaluation");
        return app;
    }

    private static async Task<Ok<EvaluationResponse>> RunAsync(
        EvaluationHarness harness,
        CancellationToken ct)
    {
        var employee = new UserPrincipal("eval-employee", ["employee"], ["engineering", "finance", "hr", "operations", "security"], Classification.Confidential);
        var restricted = new UserPrincipal("eval-guest", ["employee"], [], Classification.Public);
        var examples = GoldenDataset.Build(employee, restricted);
        var summary = await harness.RunAsync(examples, k: 5, ct).ConfigureAwait(false);

        return TypedResults.Ok(new EvaluationResponse(
            summary.RetrievalMetrics.Select(m => new RetrievalMetricsDto(m.Mode, m.RecallAtK, m.MeanReciprocalRank, m.NdcgAtK, m.K)).ToArray(),
            summary.CitationPrecision,
            summary.CitationRecall,
            summary.AnyCorrectCitationRate,
            summary.RefusalAccuracy,
            summary.Examples,
            summary.Slices
                .Select(s => new RetrievalSliceDto(
                    s.Slice,
                    s.RetrievalMetrics.Select(m => new RetrievalMetricsDto(m.Mode, m.RecallAtK, m.MeanReciprocalRank, m.NdcgAtK, m.K)).ToArray(),
                    s.Examples))
                .ToArray()));
    }
}
