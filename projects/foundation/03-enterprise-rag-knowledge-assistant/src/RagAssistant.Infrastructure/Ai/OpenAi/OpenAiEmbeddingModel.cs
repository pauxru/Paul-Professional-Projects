using System.Net.Http.Json;
using System.Text.Json.Serialization;
using RagAssistant.Application.Abstractions;
using RagAssistant.Infrastructure.Options;

namespace RagAssistant.Infrastructure.Ai.OpenAi;

public sealed class OpenAiEmbeddingModel : IEmbeddingModel
{
    private readonly HttpClient _http;
    private readonly AiOptions _options;

    public OpenAiEmbeddingModel(HttpClient http, AiOptions options)
    {
        _http = http;
        _options = options;
    }

    public string ModelId => _options.OpenAiEmbeddingModel;
    public int Dimensions => _options.EmbeddingDimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var batch = await EmbedBatchAsync([text], ct).ConfigureAwait(false);
        return batch[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (string.IsNullOrWhiteSpace(_options.OpenAiApiKey))
        {
            throw new InvalidOperationException("OpenAI API key not configured; provider is guarded and must not be reached in tests.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TrimBase(_options.OpenAiEndpoint)}/v1/embeddings")
        {
            Content = JsonContent.Create(new
            {
                model = _options.OpenAiEmbeddingModel,
                input = texts,
            }),
        };
        request.Headers.Add("Authorization", $"Bearer {_options.OpenAiApiKey}");

        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<OpenAiEmbeddingResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty embedding response");

        return body.Data.Select(d => d.Embedding).ToArray();
    }

    private static string TrimBase(string? endpoint)
        => (endpoint ?? "https://api.openai.com").TrimEnd('/');

    private sealed record OpenAiEmbeddingResponse(
        [property: JsonPropertyName("data")] IReadOnlyList<OpenAiEmbeddingData> Data);

    private sealed record OpenAiEmbeddingData(
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
