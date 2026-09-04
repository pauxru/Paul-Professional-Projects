namespace RagAssistant.Domain.Documents;

public sealed class DocumentChunk
{
    private DocumentChunk()
    {
        Id = Guid.Empty;
        DocumentId = Guid.Empty;
        Content = string.Empty;
        EmbeddingModelId = string.Empty;
        Embedding = Array.Empty<byte>();
    }

    public DocumentChunk(
        Guid id,
        Guid documentId,
        int sequence,
        int startChar,
        int endChar,
        string content,
        float[] embedding,
        string embeddingModelId,
        int embeddingDimensions)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Chunk id must be provided.", nameof(id));
        }

        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Document id must be provided.", nameof(documentId));
        }

        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (startChar < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startChar));
        }

        if (endChar < startChar)
        {
            throw new ArgumentOutOfRangeException(nameof(endChar), "endChar must be >= startChar.");
        }

        if (string.IsNullOrEmpty(content))
        {
            throw new ArgumentException("Chunk content must be provided.", nameof(content));
        }

        if (embedding is null || embedding.Length == 0)
        {
            throw new ArgumentException("Chunk embedding must be provided.", nameof(embedding));
        }

        if (embedding.Length != embeddingDimensions)
        {
            throw new ArgumentException(
                $"Embedding length {embedding.Length} does not match declared dimensions {embeddingDimensions}.",
                nameof(embedding));
        }

        Id = id;
        DocumentId = documentId;
        Sequence = sequence;
        StartChar = startChar;
        EndChar = endChar;
        Content = content;
        EmbeddingModelId = embeddingModelId ?? string.Empty;
        EmbeddingDimensions = embeddingDimensions;
        Embedding = EncodeVector(embedding);
    }

    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public int Sequence { get; private set; }
    public int StartChar { get; private set; }
    public int EndChar { get; private set; }
    public string Content { get; private set; }
    public byte[] Embedding { get; private set; }
    public string EmbeddingModelId { get; private set; }
    public int EmbeddingDimensions { get; private set; }

    public float[] DecodeEmbedding()
    {
        if (Embedding.Length == 0)
        {
            return Array.Empty<float>();
        }

        var floats = new float[Embedding.Length / sizeof(float)];
        Buffer.BlockCopy(Embedding, 0, floats, 0, Embedding.Length);
        return floats;
    }

    internal static byte[] EncodeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
