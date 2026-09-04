using RagAssistant.Application.Answering;
using RagAssistant.Domain.Documents;
using RagAssistant.Domain.Retrieval;

namespace RagAssistant.UnitTests.Answering;

public sealed class GroundingCheckerTests
{
    private static RetrievedChunk Chunk(string content, Guid? id = null) => new(
        id ?? Guid.NewGuid(),
        Guid.NewGuid(),
        "Doc",
        0,
        0,
        content.Length,
        content,
        0.5,
        Classification.Internal);

    [Fact]
    public void Verify_SupportedSentence_IsAccepted()
    {
        var checker = new LexicalGroundingChecker();
        var chunk = Chunk("Employees receive twenty days of paid time off per year.");
        var result = checker.Verify("Employees receive twenty days of paid time off.", new[] { chunk }, 0.4);

        Assert.NotEmpty(result.SupportedSentences);
        Assert.True(result.SupportRatio >= 0.5);
    }

    [Fact]
    public void Verify_UnsupportedSentence_IsRejected()
    {
        var checker = new LexicalGroundingChecker();
        var chunk = Chunk("Warehouse robotics require a two-person integrity inspection before restart.");
        var result = checker.Verify("Employees receive unlimited vacation and bonuses.", new[] { chunk }, 0.4);

        Assert.Empty(result.SupportedSentences);
        Assert.NotEmpty(result.UnsupportedSentences);
        Assert.Equal(0, result.SupportRatio);
    }

    [Fact]
    public void Verify_MixedAnswer_ComputesRatio()
    {
        var checker = new LexicalGroundingChecker();
        var chunk = Chunk("Password must be at least 14 characters and include a number.");
        var result = checker.Verify(
            "Passwords must include at least fourteen characters and a number. Also, they must be printed on paper.",
            new[] { chunk },
            0.3);
        Assert.InRange(result.SupportRatio, 0.4, 0.6);
    }

    [Fact]
    public void Verify_EmptyAnswer_ReturnsZero()
    {
        var checker = new LexicalGroundingChecker();
        var chunk = Chunk("Some content here.");
        var result = checker.Verify(string.Empty, new[] { chunk });
        Assert.Empty(result.SupportedSentences);
    }

    [Fact]
    public void Verify_NoChunks_ReturnsZero()
    {
        var checker = new LexicalGroundingChecker();
        var result = checker.Verify("An answer with words.", Array.Empty<RetrievedChunk>());
        Assert.Empty(result.SupportedSentences);
    }
}
