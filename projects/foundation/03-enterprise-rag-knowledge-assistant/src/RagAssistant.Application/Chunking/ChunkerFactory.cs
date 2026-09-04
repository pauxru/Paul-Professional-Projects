namespace RagAssistant.Application.Chunking;

public interface IChunkerFactory
{
    IChunker Create(ChunkingStrategy strategy, ChunkingOptions? options = null);
}

public sealed class DefaultChunkerFactory : IChunkerFactory
{
    public IChunker Create(ChunkingStrategy strategy, ChunkingOptions? options = null)
    {
        return strategy switch
        {
            ChunkingStrategy.FixedSize => new FixedSizeChunker(options),
            ChunkingStrategy.SentenceAware => new SentenceAwareChunker(options),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy)),
        };
    }
}
