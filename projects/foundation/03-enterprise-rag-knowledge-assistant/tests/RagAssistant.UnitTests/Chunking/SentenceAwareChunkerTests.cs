using RagAssistant.Application.Chunking;

namespace RagAssistant.UnitTests.Chunking;

public sealed class SentenceAwareChunkerTests
{
    [Fact]
    public void Chunk_RespectsSentenceBoundaries()
    {
        var text = "This is the first sentence. Here is the second one. Third sentence here. And one more.";
        var chunker = new SentenceAwareChunker(new ChunkingOptions(45, 5));
        var spans = chunker.Chunk(text);

        Assert.NotEmpty(spans);
        foreach (var span in spans)
        {
            Assert.True(span.EndChar <= text.Length);
            Assert.True(span.StartChar >= 0);
        }
    }

    [Fact]
    public void Chunk_SpanTextsMatchSourceRanges()
    {
        var text = "Alpha beta gamma. Delta epsilon zeta. Eta theta iota kappa.";
        var chunker = new SentenceAwareChunker(new ChunkingOptions(30, 0));
        var spans = chunker.Chunk(text);

        foreach (var span in spans)
        {
            Assert.Equal(text.Substring(span.StartChar, span.EndChar - span.StartChar), span.Text);
        }
    }

    [Fact]
    public void Chunk_WithOverlap_LastSpanKeepsTailOfPrevious()
    {
        var text = "Sentence one is here. Sentence two is here. Sentence three is here. Sentence four is here.";
        var chunker = new SentenceAwareChunker(new ChunkingOptions(45, 10));
        var spans = chunker.Chunk(text);

        Assert.True(spans.Count >= 2);
        var firstEnd = spans[0].EndChar;
        var secondStart = spans[1].StartChar;
        Assert.True(secondStart < firstEnd, $"expected second start ({secondStart}) < first end ({firstEnd}) due to overlap");
    }

    [Fact]
    public void Chunk_EmptyInput_ReturnsEmpty()
    {
        var chunker = new SentenceAwareChunker();
        Assert.Empty(chunker.Chunk(string.Empty));
    }

    [Fact]
    public void Chunk_LongSingleSentence_ProducesAtLeastOneChunk()
    {
        var text = new string('x', 500) + ".";
        var chunker = new SentenceAwareChunker(new ChunkingOptions(200, 20));
        var spans = chunker.Chunk(text);
        Assert.NotEmpty(spans);
    }
}
