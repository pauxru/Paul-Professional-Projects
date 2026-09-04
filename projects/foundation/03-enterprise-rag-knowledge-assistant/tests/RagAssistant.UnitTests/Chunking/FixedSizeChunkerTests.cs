using RagAssistant.Application.Chunking;

namespace RagAssistant.UnitTests.Chunking;

public sealed class FixedSizeChunkerTests
{
    [Fact]
    public void Chunk_WithShortInput_ReturnsSingleSpan()
    {
        var chunker = new FixedSizeChunker(new ChunkingOptions(50, 5));
        var spans = chunker.Chunk("Hello world");

        Assert.Single(spans);
        Assert.Equal(0, spans[0].StartChar);
        Assert.Equal(11, spans[0].EndChar);
        Assert.Equal("Hello world", spans[0].Text);
    }

    [Fact]
    public void Chunk_WithOverlap_ProducesOverlappingWindows()
    {
        var text = new string('a', 100);
        var chunker = new FixedSizeChunker(new ChunkingOptions(40, 10));
        var spans = chunker.Chunk(text);

        Assert.True(spans.Count >= 3);
        Assert.Equal(0, spans[0].StartChar);
        Assert.Equal(30, spans[1].StartChar);
        Assert.Equal(60, spans[2].StartChar);
    }

    [Fact]
    public void Chunk_LastSpanEndsAtInputLength()
    {
        var text = "abcdefghij";
        var chunker = new FixedSizeChunker(new ChunkingOptions(4, 1));
        var spans = chunker.Chunk(text);

        Assert.Equal(text.Length, spans[^1].EndChar);
    }

    [Fact]
    public void Chunk_UnicodeInput_PreservesCharacters()
    {
        var text = "Naïve façade — you're résumé done.";
        var chunker = new FixedSizeChunker(new ChunkingOptions(10, 2));
        var spans = chunker.Chunk(text);

        Assert.NotEmpty(spans);
        Assert.Equal(text.Length, spans[^1].EndChar);
        foreach (var span in spans)
        {
            Assert.Equal(text.Substring(span.StartChar, span.EndChar - span.StartChar), span.Text);
        }
    }

    [Fact]
    public void Chunk_EmptyInput_ReturnsEmpty()
    {
        var chunker = new FixedSizeChunker();
        var spans = chunker.Chunk(string.Empty);
        Assert.Empty(spans);
    }

    [Fact]
    public void ChunkingOptions_InvalidOverlap_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedSizeChunker(new ChunkingOptions(10, 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedSizeChunker(new ChunkingOptions(10, -1)));
    }
}
