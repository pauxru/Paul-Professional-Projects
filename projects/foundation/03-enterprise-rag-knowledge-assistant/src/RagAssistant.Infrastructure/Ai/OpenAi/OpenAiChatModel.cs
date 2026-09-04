using System.Net.Http.Json;
using System.Text.Json.Serialization;
using RagAssistant.Application.Abstractions;
using RagAssistant.Infrastructure.Options;

namespace RagAssistant.Infrastructure.Ai.OpenAi;

public sealed class OpenAiChatModel : IChatModel
{
    private readonly HttpClient _http;
    private readonly AiOptions _options;

    public OpenAiChatModel(HttpClient http, AiOptions options)
    {
        _http = http;
        _options = options;
    }

    public string ModelId => _options.OpenAiChatModel;

    public async Task<ChatCompletion> CompleteAsync(ChatRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (string.IsNullOrWhiteSpace(_options.OpenAiApiKey))
        {
            throw new InvalidOperationException("OpenAI API key not configured; provider is guarded and must not be reached in tests.");
        }

        var payload = new
        {
            model = _options.OpenAiChatModel,
            temperature = req.Temperature,
            max_tokens = req.MaxTokens,
            messages = req.Messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TrimBase(_options.OpenAiEndpoint)}/v1/chat/completions")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("Authorization", $"Bearer {_options.OpenAiApiKey}");

        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<OpenAiChatResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty OpenAI response");

        var choice = body.Choices.FirstOrDefault()
            ?? throw new InvalidOperationException("OpenAI response contained no choices");
        return new ChatCompletion(
            choice.Message.Content ?? string.Empty,
            body.Usage?.PromptTokens ?? 0,
            body.Usage?.CompletionTokens ?? 0,
            _options.OpenAiChatModel,
            choice.FinishReason ?? "stop");
    }

    private static string TrimBase(string? endpoint)
        => (endpoint ?? "https://api.openai.com").TrimEnd('/');

    private sealed record OpenAiChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChoice> Choices,
        [property: JsonPropertyName("usage")] OpenAiUsage? Usage);

    private sealed record OpenAiChoice(
        [property: JsonPropertyName("message")] OpenAiMessage Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record OpenAiMessage(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("content")] string? Content);

    private sealed record OpenAiUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens);
}
