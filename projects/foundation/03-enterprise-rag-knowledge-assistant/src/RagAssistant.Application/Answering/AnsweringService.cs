using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Retrieval;
using RagAssistant.Domain.Documents;
using RagAssistant.Domain.Retrieval;

namespace RagAssistant.Application.Answering;

public sealed record AnswerRequest(
    string Query,
    UserPrincipal User,
    RetrievalMode Mode = RetrievalMode.Hybrid,
    int TopK = 4,
    string? Tenant = null);

public sealed record AnswerResult(
    string Answer,
    bool Refused,
    string RefusalReason,
    double SupportRatio,
    IReadOnlyList<Citation> Citations,
    RetrievalMode Mode,
    string PromptVersion,
    string ChatModelId,
    string EmbeddingModelId,
    int PromptTokens,
    int CompletionTokens);

public sealed class AnsweringService
{
    private readonly IRetriever _retriever;
    private readonly IChatModel _chatModel;
    private readonly IEmbeddingModel _embedding;
    private readonly IGroundingChecker _groundingChecker;
    private readonly IPromptRepository _prompts;
    private readonly ITokenCounter _tokenCounter;
    private readonly RagAnsweringOptions _options;

    public AnsweringService(
        IRetriever retriever,
        IChatModel chatModel,
        IEmbeddingModel embedding,
        IGroundingChecker groundingChecker,
        IPromptRepository prompts,
        ITokenCounter tokenCounter,
        RagAnsweringOptions? options = null)
    {
        _retriever = retriever;
        _chatModel = chatModel;
        _embedding = embedding;
        _groundingChecker = groundingChecker;
        _prompts = prompts;
        _tokenCounter = tokenCounter;
        _options = options ?? new RagAnsweringOptions();
    }

    public async Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new ArgumentException("Query is required.", nameof(request));
        }

        var promptTemplate = await _prompts.GetActiveAsync(RagPrompts.DefaultPromptName, ct).ConfigureAwait(false);
        var promptVersion = promptTemplate?.Identifier ?? $"{RagPrompts.DefaultPromptName}@{RagPrompts.DefaultPromptVersion}";

        var retrieval = await _retriever.RetrieveAsync(
            new RetrievalRequest(request.Query, request.User, request.Mode, request.TopK, Math.Max(request.TopK * 4, 12)),
            ct).ConfigureAwait(false);

        if (retrieval.Chunks.Count == 0 || retrieval.Chunks.Max(c => c.Score) < _options.MinRetrievalScore)
        {
            return Refuse("insufficient-retrieval", retrieval, promptVersion, 0);
        }

        var systemPrompt = RagPrompts.EncodeContext(retrieval.Chunks);
        var userPrompt = FormatUserPrompt(promptTemplate?.Body ?? RagPrompts.DefaultPromptBody, retrieval.Chunks, request.Query);
        var chatRequest = new ChatRequest(
            new[]
            {
                new ChatMessagePart("system", systemPrompt),
                new ChatMessagePart("user", userPrompt),
            });

        var completion = await _chatModel.CompleteAsync(chatRequest, ct).ConfigureAwait(false);
        var grounding = _groundingChecker.Verify(completion.Content, retrieval.Chunks, _options.MinSupportForSentence);

        if (grounding.SupportRatio < _options.MinSupportRatio)
        {
            return Refuse("insufficient-grounding", retrieval, promptVersion, grounding.SupportRatio);
        }

        var citations = BuildCitations(retrieval.Chunks, grounding, completion.Content);

        return new AnswerResult(
            Answer: completion.Content,
            Refused: false,
            RefusalReason: string.Empty,
            SupportRatio: grounding.SupportRatio,
            Citations: citations,
            Mode: retrieval.Mode,
            PromptVersion: promptVersion,
            ChatModelId: _chatModel.ModelId,
            EmbeddingModelId: _embedding.ModelId,
            PromptTokens: completion.PromptTokens,
            CompletionTokens: completion.CompletionTokens);
    }

    private AnswerResult Refuse(string reason, RetrievalResult retrieval, string promptVersion, double supportRatio)
    {
        return new AnswerResult(
            Answer: RagPrompts.RefusalMessage,
            Refused: true,
            RefusalReason: reason,
            SupportRatio: supportRatio,
            Citations: Array.Empty<Citation>(),
            Mode: retrieval.Mode,
            PromptVersion: promptVersion,
            ChatModelId: _chatModel.ModelId,
            EmbeddingModelId: _embedding.ModelId,
            PromptTokens: _tokenCounter.CountTokens(promptVersion),
            CompletionTokens: _tokenCounter.CountTokens(RagPrompts.RefusalMessage));
    }

    private static string FormatUserPrompt(string body, IReadOnlyList<RetrievedChunk> chunks, string question)
    {
        var contextBlock = string.Join("\n", chunks.Select((c, i) => $"[{i + 1}] {c.Content.Trim()}"));
        return body
            .Replace("{context}", contextBlock, StringComparison.Ordinal)
            .Replace("{question}", question, StringComparison.Ordinal);
    }

    private static IReadOnlyList<Citation> BuildCitations(IReadOnlyList<RetrievedChunk> chunks, GroundingResult grounding, string answerText)
    {
        var markedIndices = ExtractCitationMarkers(answerText, chunks.Count);
        var supportedChunkIds = grounding.SupportedSentences
            .Where(s => s.ChunkId != Guid.Empty)
            .Select(s => s.ChunkId)
            .ToHashSet();

        if (markedIndices.Count > 0)
        {
            var citations = new List<Citation>(markedIndices.Count);
            foreach (var index in markedIndices)
            {
                var chunk = chunks[index];
                citations.Add(new Citation(chunk.DocumentId, chunk.DocumentTitle, chunk.ChunkId, chunk.Sequence, chunk.StartChar, chunk.EndChar, chunk.Score));
            }

            return citations;
        }

        if (supportedChunkIds.Count > 0)
        {
            return chunks
                .Where(c => supportedChunkIds.Contains(c.ChunkId))
                .Select(c => new Citation(c.DocumentId, c.DocumentTitle, c.ChunkId, c.Sequence, c.StartChar, c.EndChar, c.Score))
                .ToArray();
        }

        return chunks
            .Take(Math.Min(chunks.Count, 1))
            .Select(c => new Citation(c.DocumentId, c.DocumentTitle, c.ChunkId, c.Sequence, c.StartChar, c.EndChar, c.Score))
            .ToArray();
    }

    private static IReadOnlyList<int> ExtractCitationMarkers(string answerText, int chunkCount)
    {
        if (string.IsNullOrWhiteSpace(answerText) || chunkCount == 0)
        {
            return Array.Empty<int>();
        }

        var indices = new List<int>();
        var seen = new HashSet<int>();
        var i = 0;
        while (i < answerText.Length)
        {
            var open = answerText.IndexOf('[', i);
            if (open < 0)
            {
                break;
            }

            var close = answerText.IndexOf(']', open + 1);
            if (close < 0)
            {
                break;
            }

            var inside = answerText.AsSpan(open + 1, close - open - 1).Trim();
            if (int.TryParse(inside, out var n) && n >= 1 && n <= chunkCount && seen.Add(n))
            {
                indices.Add(n - 1);
            }

            i = close + 1;
        }

        return indices;
    }
}

public sealed record RagAnsweringOptions
{
    public double MinRetrievalScore { get; init; } = 0.05;
    public double MinSupportForSentence { get; init; } = 0.25;
    public double MinSupportRatio { get; init; } = 0.4;
}
