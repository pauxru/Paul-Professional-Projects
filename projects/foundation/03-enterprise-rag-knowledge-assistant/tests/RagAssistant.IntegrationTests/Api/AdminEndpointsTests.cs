using System.Net;
using System.Net.Http.Json;
using RagAssistant.Api.Contracts;
using RagAssistant.Domain.Documents;
using RagAssistant.IntegrationTests.Fixtures;

namespace RagAssistant.IntegrationTests.Api;

public sealed class AdminEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public AdminEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ListPrompts_NonAdmin_Returns403()
    {
        var client = await _factory.AuthenticatedClientAsync(
            "alice", ["employee"], ["hr"], Classification.Internal);
        var response = await client.GetAsync("/api/v1/admin/prompts");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RegisterPrompt_AsAdmin_Succeeds()
    {
        var client = await _factory.AuthenticatedClientAsync(
            "admin", ["employee", "admin"], ["hr"], Classification.Confidential);

        var payload = new PromptRequestDto("rag.answer", "v-" + Guid.NewGuid().ToString("N")[..8], "You are a helpful assistant. Content: " + Guid.NewGuid());
        var response = await client.PostAsJsonAsync("/api/v1/admin/prompts", payload);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PromptResponse>(TestAuth.Json);
        Assert.NotNull(body);
        Assert.True(body!.IsActive);
    }

    [Fact]
    public async Task RunEvaluation_ReportsMetricsAndHybridBeatsDenseOnMrr()
    {
        var client = await _factory.AuthenticatedClientAsync(
            "admin", ["employee", "admin"], [], Classification.Confidential);
        var response = await client.PostAsJsonAsync("/api/v1/eval/runs", new { });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<EvaluationResponse>(TestAuth.Json);
        Assert.NotNull(payload);
        Assert.NotEmpty(payload!.RetrievalMetrics);

        var hybrid = payload.RetrievalMetrics.First(m => m.Mode.Equals("Hybrid", StringComparison.OrdinalIgnoreCase));
        var dense = payload.RetrievalMetrics.First(m => m.Mode.Equals("Dense", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            hybrid.MeanReciprocalRank + 0.0001 >= dense.MeanReciprocalRank,
            $"Hybrid MRR={hybrid.MeanReciprocalRank:F3} should be >= Dense MRR={dense.MeanReciprocalRank:F3}");

        Assert.True(hybrid.RecallAtK >= 0.7, $"Hybrid Recall@5={hybrid.RecallAtK:F3} should be >= 0.7");
        Assert.True(payload.RefusalAccuracy >= 0.8, $"Refusal accuracy {payload.RefusalAccuracy:F3} should be >= 0.8");
        Assert.True(
            payload.CitationPrecision >= 0.6,
            $"Citation precision {payload.CitationPrecision:F3} should be >= 0.6 after the citation-marker fix");
        Assert.True(
            payload.CitationRecall >= payload.CitationPrecision - 0.35,
            $"Citation recall {payload.CitationRecall:F3} should be within 0.35 of precision {payload.CitationPrecision:F3}");
        Assert.True(
            payload.AnyCorrectCitationRate >= 0.75,
            $"Any-correct-citation rate {payload.AnyCorrectCitationRate:F3} should be >= 0.75");

        var paraphrase = payload.Slices.FirstOrDefault(s => s.Slice.Equals("paraphrase", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(paraphrase);
        var pHybrid = paraphrase!.RetrievalMetrics.First(m => m.Mode.Equals("Hybrid", StringComparison.OrdinalIgnoreCase));
        var pDense = paraphrase.RetrievalMetrics.First(m => m.Mode.Equals("Dense", StringComparison.OrdinalIgnoreCase));
        Assert.True(
            pHybrid.MeanReciprocalRank + 0.0001 >= pDense.MeanReciprocalRank,
            $"On paraphrase slice, hybrid MRR={pHybrid.MeanReciprocalRank:F3} should be >= dense MRR={pDense.MeanReciprocalRank:F3}");
    }
}
