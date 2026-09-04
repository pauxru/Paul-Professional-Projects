using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Answering;
using RagAssistant.Domain.Documents;
using RagAssistant.Domain.Retrieval;

namespace RagAssistant.UnitTests.Answering;

public sealed class TemplateChatModelTests
{
    private static RetrievedChunk Chunk(string content, string title = "Doc", double score = 0.8) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        title,
        0,
        0,
        content.Length,
        content,
        score,
        Classification.Internal);

    [Fact]
    public async Task CompleteAsync_WithContext_ReturnsAnswerWithCitationMarkers()
    {
        var model = new TemplateChatModel();
        var chunks = new[]
        {
            Chunk("Employees receive twenty days of paid time off."),
            Chunk("Sick leave is separate from PTO and covers ten days per year."),
        };

        var request = new ChatRequest(new[]
        {
            new ChatMessagePart("system", RagPrompts.EncodeContext(chunks)),
            new ChatMessagePart("user", "How much paid time off do employees receive?"),
        });

        var completion = await model.CompleteAsync(request, CancellationToken.None);
        Assert.Contains("[1]", completion.Content);
        Assert.Contains("paid time off", completion.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteAsync_WithoutContext_RefusesGracefully()
    {
        var model = new TemplateChatModel();
        var request = new ChatRequest(new[]
        {
            new ChatMessagePart("system", "no marker in this text"),
            new ChatMessagePart("user", "What is anything?"),
        });

        var completion = await model.CompleteAsync(request, CancellationToken.None);
        Assert.Equal(RagPrompts.RefusalMessage, completion.Content);
    }

    [Fact]
    public async Task CompleteAsync_ReportsModelIdAndFinish()
    {
        var model = new TemplateChatModel();
        var request = new ChatRequest(new[]
        {
            new ChatMessagePart("system", RagPrompts.EncodeContext(new[] { Chunk("Alpha beta gamma.") })),
            new ChatMessagePart("user", "alpha"),
        });

        var completion = await model.CompleteAsync(request, CancellationToken.None);
        Assert.Equal(TemplateChatModel.DefaultModelId, completion.Model);
        Assert.Equal("stop", completion.FinishReason);
        Assert.True(completion.PromptTokens > 0);
    }

    [Fact]
    public async Task CompleteAsync_PrefersHighScoringChunks_IgnoresLowScoreNoise()
    {
        var model = new TemplateChatModel();
        var chunks = new[]
        {
            Chunk("Every full-time employee accrues 20 days of paid time off per year.", "HR PTO", score: 0.95),
            Chunk("Paid time off can be carried over up to 5 days into the next year.",  "HR PTO", score: 0.90),
            Chunk("Passwords must be at least 14 characters long.",                      "IT Passwords", score: 0.05),
        };
        var request = new ChatRequest(new[]
        {
            new ChatMessagePart("system", RagPrompts.EncodeContext(chunks)),
            new ChatMessagePart("user", "How much paid time off do full-time employees receive?"),
        });

        var completion = await model.CompleteAsync(request, CancellationToken.None);

        Assert.DoesNotContain("[3]", completion.Content);
        Assert.DoesNotContain("passwords", completion.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("paid time off", completion.Content, StringComparison.OrdinalIgnoreCase);
    }
}
