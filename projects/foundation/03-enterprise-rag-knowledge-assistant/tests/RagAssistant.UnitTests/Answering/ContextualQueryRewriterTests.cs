using RagAssistant.Application.Chat;
using RagAssistant.Domain.Chat;

namespace RagAssistant.UnitTests.Answering;

public sealed class ContextualQueryRewriterTests
{
    [Fact]
    public void Rewrite_NoHistory_ReturnsQueryUnchanged()
    {
        var rewriter = new ContextualQueryRewriter();
        var result = rewriter.Rewrite("What is PTO?", Array.Empty<ChatMessage>());
        Assert.Equal("What is PTO?", result);
    }

    [Fact]
    public void Rewrite_PronounFollowUp_ExpandsWithPrevious()
    {
        var rewriter = new ContextualQueryRewriter();
        var history = new[]
        {
            new ChatMessage(Guid.NewGuid(), Guid.NewGuid(), ChatMessageRole.User, "Tell me about paid time off", DateTimeOffset.UtcNow, null),
            new ChatMessage(Guid.NewGuid(), Guid.NewGuid(), ChatMessageRole.Assistant, "Paid time off is 20 days.", DateTimeOffset.UtcNow, "rag.answer@v1"),
        };

        var result = rewriter.Rewrite("What is it capped at?", history);
        Assert.Contains("referring to", result);
        Assert.Contains("paid time off", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rewrite_ContinuationPhrase_ConcatenatesPrevious()
    {
        var rewriter = new ContextualQueryRewriter();
        var history = new[]
        {
            new ChatMessage(Guid.NewGuid(), Guid.NewGuid(), ChatMessageRole.User, "How many sick days?", DateTimeOffset.UtcNow, null),
        };
        var result = rewriter.Rewrite("what about carry-over?", history);
        Assert.Contains("How many sick days", result);
        Assert.Contains("carry-over", result);
    }
}
