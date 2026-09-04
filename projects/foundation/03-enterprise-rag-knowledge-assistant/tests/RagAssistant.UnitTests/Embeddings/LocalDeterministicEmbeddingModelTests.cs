using RagAssistant.Application.Embeddings;

namespace RagAssistant.UnitTests.Embeddings;

public sealed class LocalDeterministicEmbeddingModelTests
{
    [Fact]
    public async Task Embed_IsDeterministic()
    {
        var model = new LocalDeterministicEmbeddingModel();
        var a = await model.EmbedAsync("Employees receive twenty days of paid time off.", CancellationToken.None);
        var b = await model.EmbedAsync("Employees receive twenty days of paid time off.", CancellationToken.None);

        Assert.Equal(a.Length, b.Length);
        for (var i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i], b[i], precision: 6);
        }
    }

    [Fact]
    public async Task Embed_IsL2Normalised()
    {
        var model = new LocalDeterministicEmbeddingModel();
        var vector = await model.EmbedAsync("Security incident escalation process notifies CISO within thirty minutes.", CancellationToken.None);
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        Assert.InRange(norm, 0.98, 1.02);
    }

    [Fact]
    public async Task Embed_SimilarSentencesHaveHigherCosineThanUnrelated()
    {
        var model = new LocalDeterministicEmbeddingModel();
        var q = await model.EmbedAsync("How many paid time off days do employees receive?", CancellationToken.None);
        var similar = await model.EmbedAsync("Every full-time employee accrues twenty days of paid time off per year.", CancellationToken.None);
        var unrelated = await model.EmbedAsync("Warehouse robots require safety lockouts and physical inspection.", CancellationToken.None);

        var simSimilar = VectorMath.CosineSimilarity(q, similar);
        var simUnrelated = VectorMath.CosineSimilarity(q, unrelated);

        Assert.True(simSimilar > simUnrelated,
            $"Similar sentence cosine ({simSimilar}) should be greater than unrelated ({simUnrelated}).");
    }

    [Fact]
    public async Task Embed_OrderingPropertyHoldsOnCorpus()
    {
        var model = new LocalDeterministicEmbeddingModel();
        var query = await model.EmbedAsync("What is the dinner reimbursement cap?", CancellationToken.None);
        var relevant = await model.EmbedAsync("Employees may claim dinner up to 45 USD per day.", CancellationToken.None);
        var irrelevant = await model.EmbedAsync("Access badges are issued to employees on their first day.", CancellationToken.None);

        var scoreRelevant = VectorMath.CosineSimilarity(query, relevant);
        var scoreIrrelevant = VectorMath.CosineSimilarity(query, irrelevant);

        Assert.True(scoreRelevant > scoreIrrelevant, $"Relevant ({scoreRelevant}) must beat irrelevant ({scoreIrrelevant}).");
    }

    [Fact]
    public async Task Embed_BatchMatchesSingle()
    {
        var model = new LocalDeterministicEmbeddingModel();
        var texts = new[] { "alpha beta", "gamma delta", "epsilon zeta" };
        var batch = await model.EmbedBatchAsync(texts, CancellationToken.None);

        for (var i = 0; i < texts.Length; i++)
        {
            var single = await model.EmbedAsync(texts[i], CancellationToken.None);
            for (var j = 0; j < single.Length; j++)
            {
                Assert.Equal(single[j], batch[i][j], precision: 6);
            }
        }
    }
}
