namespace RagAssistant.Application.Abstractions;

public interface IEmbeddingModel
{
    string ModelId { get; }
    int Dimensions { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
