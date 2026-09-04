namespace RagAssistant.Application.Chunking;

public sealed record ChunkingOptions(int MaxChars, int Overlap)
{
    public static ChunkingOptions Default { get; } = new(600, 100);

    public void Validate()
    {
        if (MaxChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxChars), "MaxChars must be positive.");
        }

        if (Overlap < 0 || Overlap >= MaxChars)
        {
            throw new ArgumentOutOfRangeException(nameof(Overlap), "Overlap must be in [0, MaxChars).");
        }
    }
}

public sealed class FixedSizeChunker : IChunker
{
    private readonly ChunkingOptions _options;

    public FixedSizeChunker(ChunkingOptions? options = null)
    {
        _options = options ?? ChunkingOptions.Default;
        _options.Validate();
    }

    public ChunkingStrategy Strategy => ChunkingStrategy.FixedSize;

    public IReadOnlyList<ChunkSpan> Chunk(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<ChunkSpan>();
        }

        var spans = new List<ChunkSpan>();
        var step = Math.Max(1, _options.MaxChars - _options.Overlap);
        var seq = 0;
        for (var start = 0; start < text.Length; start += step)
        {
            var end = Math.Min(text.Length, start + _options.MaxChars);
            var slice = text.Substring(start, end - start);
            spans.Add(new ChunkSpan(seq++, start, end, slice));
            if (end == text.Length)
            {
                break;
            }
        }

        return spans;
    }
}
