using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Common;
using RagAssistant.Domain.Retrieval;

namespace RagAssistant.Application.Answering;

public sealed class TemplateChatModel : IChatModel
{
    public const string DefaultModelId = "template-extractive-v1";

    public string ModelId => DefaultModelId;

    public Task<ChatCompletion> CompleteAsync(ChatRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var latest = req.Messages.LastOrDefault(m => m.Role == "user")
            ?? req.Messages.LastOrDefault()
            ?? new ChatMessagePart("user", string.Empty);

        var context = ExtractContext(req);
        var question = latest.Content;

        var answer = context.Count == 0
            ? RagPrompts.RefusalMessage
            : Compose(question, context);

        var completion = new ChatCompletion(
            answer,
            PromptTokens: EstimateTokens(req),
            CompletionTokens: EstimateTokens(answer),
            Model: ModelId,
            FinishReason: "stop");
        return Task.FromResult(completion);
    }

    private static IReadOnlyList<RetrievedChunk> ExtractContext(ChatRequest req)
    {
        var systemMessage = req.Messages.FirstOrDefault(m => m.Role == "system");
        if (systemMessage is null || string.IsNullOrEmpty(systemMessage.Content))
        {
            return Array.Empty<RetrievedChunk>();
        }

        return RagPrompts.TryDeserializeContext(systemMessage.Content, out var chunks)
            ? chunks
            : Array.Empty<RetrievedChunk>();
    }

    private static string Compose(string question, IReadOnlyList<RetrievedChunk> context)
    {
        if (context.Count == 0)
        {
            return RagPrompts.RefusalMessage;
        }

        var topScore = context.Max(c => c.Score);
        if (topScore <= 0)
        {
            topScore = 1;
        }

        var rankedChunks = context
            .Select((chunk, index) => (Chunk: chunk, Index: index))
            .OrderByDescending(x => x.Chunk.Score)
            .Take(Math.Min(3, context.Count))
            .ToArray();

        var minAcceptableScore = rankedChunks[0].Chunk.Score * 0.35;
        var candidateChunks = rankedChunks
            .Where(x => x.Chunk.Score >= minAcceptableScore)
            .ToArray();

        var questionTokens = new HashSet<string>(TextTokenizer.Tokenize(question), StringComparer.Ordinal);
        var sentences = new List<(int ChunkIndex, string Text, double Score, double ChunkScore)>();
        foreach (var (chunk, chunkIndex) in candidateChunks)
        {
            var chunkWeight = 0.4 + 0.6 * (chunk.Score / topScore);
            foreach (var sentence in LexicalGroundingChecker.SplitSentences(chunk.Content))
            {
                var tokens = new HashSet<string>(TextTokenizer.Tokenize(sentence), StringComparer.Ordinal);
                if (tokens.Count == 0)
                {
                    continue;
                }

                var overlapCount = questionTokens.Count == 0 ? 0.0 : questionTokens.Intersect(tokens).Count();
                var overlap = questionTokens.Count == 0 ? 0.0 : overlapCount / questionTokens.Count;
                if (overlap <= 0 && candidateChunks.Length > 1)
                {
                    continue;
                }

                var score = (overlap + 0.05) * chunkWeight;
                sentences.Add((chunkIndex, sentence, score, chunk.Score));
            }
        }

        if (sentences.Count == 0)
        {
            var fallback = candidateChunks[0].Chunk.Content.Trim();
            return $"{fallback} [{candidateChunks[0].Index + 1}]";
        }

        var top = sentences
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.ChunkScore)
            .Take(3)
            .OrderBy(s => s.ChunkIndex)
            .ToArray();

        var sb = new System.Text.StringBuilder();
        foreach (var s in top)
        {
            var trimmed = s.Text.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            sb.Append(trimmed);
            if (!trimmed.EndsWith('.') && !trimmed.EndsWith('!') && !trimmed.EndsWith('?'))
            {
                sb.Append('.');
            }

            sb.Append(" [").Append(s.ChunkIndex + 1).Append("] ");
        }

        return sb.ToString().Trim();
    }

    private static int EstimateTokens(ChatRequest req)
    {
        var counter = new HeuristicTokenCounter();
        return req.Messages.Sum(m => counter.CountTokens(m.Content));
    }

    private static int EstimateTokens(string text) => new HeuristicTokenCounter().CountTokens(text);
}
