namespace RagAssistant.Application.Chunking;

public sealed class SentenceAwareChunker : IChunker
{
    private static readonly char[] SentenceTerminators = ['.', '!', '?'];
    private readonly ChunkingOptions _options;

    public SentenceAwareChunker(ChunkingOptions? options = null)
    {
        _options = options ?? ChunkingOptions.Default;
        _options.Validate();
    }

    public ChunkingStrategy Strategy => ChunkingStrategy.SentenceAware;

    public IReadOnlyList<ChunkSpan> Chunk(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<ChunkSpan>();
        }

        var sentences = SplitSentences(text);
        if (sentences.Count == 0)
        {
            return Array.Empty<ChunkSpan>();
        }

        var spans = new List<ChunkSpan>();
        var seq = 0;
        var bufferStart = sentences[0].start;
        var bufferEnd = sentences[0].start;
        var buffer = new System.Text.StringBuilder();

        foreach (var (start, end) in sentences)
        {
            var length = end - start;
            if (buffer.Length + length > _options.MaxChars && buffer.Length > 0)
            {
                spans.Add(new ChunkSpan(seq++, bufferStart, bufferEnd, buffer.ToString()));

                if (_options.Overlap > 0 && buffer.Length > _options.Overlap)
                {
                    var overlapText = buffer.ToString(buffer.Length - _options.Overlap, _options.Overlap);
                    var overlapStart = bufferEnd - _options.Overlap;
                    buffer.Clear();
                    buffer.Append(overlapText);
                    bufferStart = overlapStart;
                }
                else
                {
                    buffer.Clear();
                    bufferStart = start;
                }
            }

            if (buffer.Length == 0)
            {
                bufferStart = start;
            }

            buffer.Append(text, start, length);
            bufferEnd = end;
        }

        if (buffer.Length > 0)
        {
            spans.Add(new ChunkSpan(seq, bufferStart, bufferEnd, buffer.ToString()));
        }

        return spans;
    }

    private static List<(int start, int end)> SplitSentences(string text)
    {
        var results = new List<(int, int)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (Array.IndexOf(SentenceTerminators, c) >= 0 || c == '\n')
            {
                var end = i + 1;
                if (end > start)
                {
                    results.Add((start, end));
                }
                start = end;
            }
        }

        if (start < text.Length)
        {
            results.Add((start, text.Length));
        }

        return results;
    }
}
