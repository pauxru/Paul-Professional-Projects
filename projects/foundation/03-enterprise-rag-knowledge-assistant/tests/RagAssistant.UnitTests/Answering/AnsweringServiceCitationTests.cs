using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Answering;
using RagAssistant.Application.Retrieval;
using RagAssistant.Domain.Documents;
using RagAssistant.Domain.Prompts;
using RagAssistant.Domain.Retrieval;

namespace RagAssistant.UnitTests.Answering;

public sealed class AnsweringServiceCitationTests
{
    [Fact]
    public async Task Citations_OnlyIncludeChunksMarkedInAnswerText()
    {
        var chunks = new[]
        {
            NewChunk("HR PTO chunk about paid time off.", "HR Policy: Paid time off", 0.95),
            NewChunk("Remote work chunk about hybrid schedules.", "HR Policy: Remote work", 0.5),
            NewChunk("Password reset chunk about IT support.", "IT Support: Password reset", 0.4),
        };

        var retriever = new StaticRetriever(chunks);
        // Answer references only chunk [1]. Citations should therefore only be HR PTO,
        // even though the grounding checker could match other chunks by token overlap.
        var chat = new StaticChatModel("Employees receive 20 days of paid time off. [1]");
        var embedding = new DeterministicStubEmbedding();
        var prompts = new NullPromptRepository();
        var tokens = new HeuristicTokenCounter();
        var grounding = new LexicalGroundingChecker();

        var svc = new AnsweringService(retriever, chat, embedding, grounding, prompts, tokens,
            new RagAnsweringOptions { MinRetrievalScore = 0.01, MinSupportRatio = 0.0, MinSupportForSentence = 0.0 });

        var user = new UserPrincipal("u1", ["employee"], ["hr"], Classification.Internal);
        var result = await svc.AnswerAsync(new AnswerRequest("How much PTO?", user), CancellationToken.None);

        Assert.False(result.Refused, result.RefusalReason);
        Assert.Single(result.Citations);
        Assert.Equal("HR Policy: Paid time off", result.Citations[0].DocumentTitle);
    }

    [Fact]
    public async Task Citations_IncludeAllDistinctMarkersInOrder()
    {
        var chunks = new[]
        {
            NewChunk("Alpha chunk.", "Doc A", 0.9),
            NewChunk("Beta chunk.",  "Doc B", 0.85),
            NewChunk("Gamma chunk.", "Doc C", 0.8),
        };

        var retriever = new StaticRetriever(chunks);
        // Reference chunks 1 and 3 twice — dedupe expected; chunk 2 not referenced.
        var chat = new StaticChatModel("First sentence about alpha. [1] Third sentence about gamma. [3] Extra reference to alpha. [1]");
        var embedding = new DeterministicStubEmbedding();
        var svc = new AnsweringService(retriever, chat, embedding, new LexicalGroundingChecker(),
            new NullPromptRepository(), new HeuristicTokenCounter(),
            new RagAnsweringOptions { MinRetrievalScore = 0.01, MinSupportRatio = 0.0, MinSupportForSentence = 0.0 });

        var user = new UserPrincipal("u1", ["employee"], [], Classification.Internal);
        var result = await svc.AnswerAsync(new AnswerRequest("anything", user), CancellationToken.None);

        Assert.False(result.Refused, result.RefusalReason);
        Assert.Equal(2, result.Citations.Count);
        Assert.Equal("Doc A", result.Citations[0].DocumentTitle);
        Assert.Equal("Doc C", result.Citations[1].DocumentTitle);
    }

    [Fact]
    public async Task Citations_IgnoreOutOfRangeMarkers()
    {
        var chunks = new[]
        {
            NewChunk("Only chunk.", "Doc Only", 0.9),
        };

        var retriever = new StaticRetriever(chunks);
        // [99] is out of range — must be ignored.
        var chat = new StaticChatModel("Sentence [1] and [99] and [xyz].");
        var embedding = new DeterministicStubEmbedding();
        var svc = new AnsweringService(retriever, chat, embedding, new LexicalGroundingChecker(),
            new NullPromptRepository(), new HeuristicTokenCounter(),
            new RagAnsweringOptions { MinRetrievalScore = 0.01, MinSupportRatio = 0.0, MinSupportForSentence = 0.0 });

        var user = new UserPrincipal("u1", ["employee"], [], Classification.Internal);
        var result = await svc.AnswerAsync(new AnswerRequest("anything", user), CancellationToken.None);

        Assert.False(result.Refused, result.RefusalReason);
        Assert.Single(result.Citations);
        Assert.Equal("Doc Only", result.Citations[0].DocumentTitle);
    }

    private static RetrievedChunk NewChunk(string content, string title, double score) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, 0, 0, content.Length, content, score, Classification.Internal);

    private sealed class StaticRetriever(IReadOnlyList<RetrievedChunk> chunks) : IRetriever
    {
        public Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken ct)
            => Task.FromResult(new RetrievalResult(chunks, request.Mode, chunks.Count));
    }

    private sealed class StaticChatModel(string answer) : IChatModel
    {
        public string ModelId => "static-test";
        public Task<ChatCompletion> CompleteAsync(ChatRequest req, CancellationToken ct)
            => Task.FromResult(new ChatCompletion(answer, 1, 1, ModelId, "stop"));
    }

    private sealed class DeterministicStubEmbedding : IEmbeddingModel
    {
        public string ModelId => "stub-embed";
        public int Dimensions => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct)
            => Task.FromResult(new float[] { 1, 0, 0, 0 });
        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[] { 1, 0, 0, 0 }).ToArray());
    }

    private sealed class NullPromptRepository : IPromptRepository
    {
        public Task<PromptTemplate?> GetActiveAsync(string name, CancellationToken ct) => Task.FromResult<PromptTemplate?>(null);
        public Task<PromptTemplate?> GetByIdentifierAsync(string name, string version, CancellationToken ct) => Task.FromResult<PromptTemplate?>(null);
        public Task<IReadOnlyList<PromptTemplate>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PromptTemplate>>(Array.Empty<PromptTemplate>());
        public Task AddAsync(PromptTemplate template, CancellationToken ct) => Task.CompletedTask;
        public Task DeactivateOthersAsync(string name, Guid activeId, CancellationToken ct) => Task.CompletedTask;
    }
}
