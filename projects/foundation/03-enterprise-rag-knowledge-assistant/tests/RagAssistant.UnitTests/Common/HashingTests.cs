using RagAssistant.Application.Common;

namespace RagAssistant.UnitTests.Common;

public sealed class HashingTests
{
    [Fact]
    public void ComputeContentHash_IsStableForEquivalentWhitespace()
    {
        var a = Hashing.ComputeContentHash("Hello world");
        var b = Hashing.ComputeContentHash("Hello    world");
        var c = Hashing.ComputeContentHash("Hello   world");
        Assert.Equal(a, b);
        Assert.Equal(b, c);
    }

    [Fact]
    public void ComputeContentHash_ChangesWhenContentChanges()
    {
        var a = Hashing.ComputeContentHash("Alpha");
        var b = Hashing.ComputeContentHash("Beta");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputePromptHash_IsStableAcrossLineEndings()
    {
        var a = Hashing.ComputePromptHash("line1\nline2");
        var b = Hashing.ComputePromptHash("line1\r\nline2");
        Assert.Equal(a, b);
    }
}
