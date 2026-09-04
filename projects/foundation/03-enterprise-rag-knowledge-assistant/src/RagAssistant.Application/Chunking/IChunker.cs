namespace RagAssistant.Application.Chunking;

public sealed record ChunkSpan(int Sequence, int StartChar, int EndChar, string Text);

public enum ChunkingStrategy
{
    FixedSize = 0,
    SentenceAware = 1,
}

public interface IChunker
{
    ChunkingStrategy Strategy { get; }
    IReadOnlyList<ChunkSpan> Chunk(string text);
}
