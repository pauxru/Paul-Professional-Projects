using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPlatform.Application.Abstractions;
using AgentPlatform.Domain.Models;

namespace AgentPlatform.Infrastructure.Models;

/// <summary>Configuration for the real model providers. Bound from config; empty by default.</summary>
public sealed class ModelProviderOptions
{
    /// <summary>Which provider to use: "mock" (default), "openai" or "azure".</summary>
    public string Provider { get; set; } = "mock";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o-mini";

    // OpenAI
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";

    // Azure OpenAI
    public string AzureEndpoint { get; set; } = string.Empty;
    public string AzureDeployment { get; set; } = string.Empty;
    public string AzureApiVersion { get; set; } = "2024-06-01";
}

/// <summary>
/// Shared translation between our provider-agnostic <see cref="ChatRequest"/>/<see cref="ChatCompletion"/>
/// and the OpenAI chat-completions wire format. Kept pure and side-effect free so it is unit-testable.
/// </summary>
internal static class OpenAiWire
{
    public static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static string BuildRequestBody(ChatRequest request, string model)
    {
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            var obj = new JsonObject { ["role"] = RoleName(message.Role) };
            if (message.Content is not null) obj["content"] = message.Content;
            if (message.ToolCallId is not null) obj["tool_call_id"] = message.ToolCallId;
            if (message.Name is not null) obj["name"] = message.Name;
            if (message.ToolCalls.Count > 0)
            {
                var calls = new JsonArray();
                foreach (var call in message.ToolCalls)
                    calls.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.ToolName, ["arguments"] = call.ArgumentsJson },
                    });
                obj["tool_calls"] = calls;
            }
            messages.Add(obj);
        }

        var tools = new JsonArray();
        foreach (var tool in request.Tools)
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.ParametersSchemaJson),
                },
            });

        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = request.Options.Temperature,
            ["max_tokens"] = request.Options.MaxOutputTokens,
            ["messages"] = messages,
        };
        if (tools.Count > 0) body["tools"] = tools;
        return body.ToJsonString(Json);
    }

    public static ChatCompletion ParseResponse(string json, string modelId)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("Model response was not a JSON object.");

        var choice = (root["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var message = choice?["message"] as JsonObject;
        var finish = choice?["finish_reason"]?.GetValue<string>();

        var toolCalls = new List<ModelToolCall>();
        if (message?["tool_calls"] is JsonArray callArray)
        {
            foreach (var node in callArray)
            {
                if (node is not JsonObject call) continue;
                var fn = call["function"] as JsonObject;
                toolCalls.Add(new ModelToolCall(
                    call["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n"),
                    fn?["name"]?.GetValue<string>() ?? "unknown",
                    fn?["arguments"]?.GetValue<string>() ?? "{}"));
            }
        }

        var usage = root["usage"] as JsonObject;
        return new ChatCompletion
        {
            Content = message?["content"]?.GetValue<string>(),
            ToolCalls = toolCalls,
            FinishReason = MapFinish(finish, toolCalls.Count > 0),
            ModelId = modelId,
            Usage = new TokenUsage(
                usage?["prompt_tokens"]?.GetValue<int>() ?? 0,
                usage?["completion_tokens"]?.GetValue<int>() ?? 0),
        };
    }

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => "user",
    };

    private static FinishReason MapFinish(string? finish, bool hasToolCalls) => finish switch
    {
        "tool_calls" => FinishReason.ToolCalls,
        "length" => FinishReason.Length,
        "content_filter" => FinishReason.ContentFilter,
        "stop" when hasToolCalls => FinishReason.ToolCalls,
        "stop" => FinishReason.Stop,
        _ => hasToolCalls ? FinishReason.ToolCalls : FinishReason.Stop,
    };
}

/// <summary>
/// OpenAI chat-completions adapter. Compiled and unit-tested against a stubbed
/// <see cref="HttpMessageHandler"/>, but never used unless <c>Model:Provider = openai</c>.
/// </summary>
public sealed class OpenAiChatModel : IChatModel
{
    private readonly HttpClient _http;
    private readonly ModelProviderOptions _options;

    public OpenAiChatModel(HttpClient http, ModelProviderOptions options)
    {
        _http = http;
        _options = options;
    }

    public string ModelId => $"openai:{_options.Model}";

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var body = OpenAiWire.BuildRequestBody(request, _options.Model);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_options.OpenAiBaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var response = await _http.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return OpenAiWire.ParseResponse(payload, ModelId);
    }
}

/// <summary>
/// Azure OpenAI adapter — same wire format as OpenAI but a deployment-scoped URL and an
/// <c>api-key</c> header. Compiled and unit-tested, never reached by default.
/// </summary>
public sealed class AzureOpenAiChatModel : IChatModel
{
    private readonly HttpClient _http;
    private readonly ModelProviderOptions _options;

    public AzureOpenAiChatModel(HttpClient http, ModelProviderOptions options)
    {
        _http = http;
        _options = options;
    }

    public string ModelId => $"azure:{_options.AzureDeployment}";

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var body = OpenAiWire.BuildRequestBody(request, _options.Model);
        var url = $"{_options.AzureEndpoint.TrimEnd('/')}/openai/deployments/{_options.AzureDeployment}/chat/completions?api-version={_options.AzureApiVersion}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Add("api-key", _options.ApiKey);

        using var response = await _http.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return OpenAiWire.ParseResponse(payload, ModelId);
    }
}
