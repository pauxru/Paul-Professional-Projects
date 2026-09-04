using RagAssistant.Domain.Documents;

namespace RagAssistant.UnitTests.Common;

public sealed class DocumentTests
{
    [Fact]
    public void UpdateContent_IncrementsVersion()
    {
        var doc = new Document(
            Guid.NewGuid(),
            "Doc",
            "src",
            "content",
            "hash1",
            new AccessControlList(),
            DateTimeOffset.UtcNow);

        doc.UpdateContent("new content", "hash2", new AccessControlList(), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(2, doc.Version);
        Assert.Empty(doc.Chunks);
    }

    [Fact]
    public void Constructor_EmptyId_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new Document(Guid.Empty, "t", "src", "content", "hash", new AccessControlList(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void DocumentChunk_EncodesAndDecodesEmbedding()
    {
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        var chunk = new DocumentChunk(
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            0,
            10,
            "content",
            embedding,
            "model",
            embedding.Length);

        var decoded = chunk.DecodeEmbedding();
        Assert.Equal(embedding.Length, decoded.Length);
        for (var i = 0; i < embedding.Length; i++)
        {
            Assert.Equal(embedding[i], decoded[i]);
        }
    }

    [Fact]
    public void DocumentChunk_MismatchedDims_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new DocumentChunk(Guid.NewGuid(), Guid.NewGuid(), 0, 0, 10, "content", new float[] { 0.1f, 0.2f }, "model", 3));
    }
}
