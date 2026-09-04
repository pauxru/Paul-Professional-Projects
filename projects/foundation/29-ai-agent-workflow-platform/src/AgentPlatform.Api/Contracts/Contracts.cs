using System.Text.Json.Nodes;
using AgentPlatform.Application.Engine;
using AgentPlatform.Application.Evaluation;
using AgentPlatform.Domain.Approvals;
using AgentPlatform.Domain.Runs;
using AgentPlatform.Domain.Tools;
using AgentPlatform.Domain.Tracing;
using AgentPlatform.Domain.Workflows;

namespace AgentPlatform.Api.Contracts;

// ---------------------------------------------------------------- requests

public sealed record StartRunRequest
{
    public string? WorkflowName { get; init; }
    public int? Version { get; init; }
    public JsonObject? Inputs { get; init; }
    public string? IdempotencyKey { get; init; }
}

public sealed record ApprovalDecisionRequest
{
    public string? Notes { get; init; }
    public JsonObject? ModifiedArguments { get; init; }
}

public sealed record DevTokenRequest
{
    public string? Subject { get; init; }
    public string? Tenant { get; init; }
    public string[]? Scopes { get; init; }
}

public sealed record EvalRunRequest
{
    public string? Workflow { get; init; }
}

// ---------------------------------------------------------------- responses

public sealed record RunResponse(
    string Id, string Workflow, int Version, string Status, string? Outcome, string? CurrentStepId,
    string? Message, int Tokens, decimal Cost, int ToolCalls, int ModelCalls,
    string TenantId, string CorrelationId, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record TraceEventResponse(
    int Ordinal, string Type, string? StepId, DateTimeOffset Timestamp, long DurationMs,
    int PromptTokens, int CompletionTokens, decimal Cost, string? PromptVersion, string? Tool,
    bool Success, JsonNode? Data);

public sealed record ApprovalResponse(
    string Id, string RunId, string StepId, string TenantId, string Title, string Status,
    string RiskLevel, JsonNode? ProposedAction, string ReasoningTrace, DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? DecidedAt, string? DecidedBy);

public sealed record ToolResponse(
    string Name, string Version, string Description, string SideEffect, string RiskLevel,
    IReadOnlyList<string> RequiredScopes, bool RequiresApproval, bool IdempotentByNature,
    int RateLimitPerMinute, decimal CostWeight, JsonNode? ParameterSchema);

public sealed record PromptResponse(string Id, string Name, int Version, string Description,
    IReadOnlyList<string> Variables, string Body);

public sealed record WorkflowResponse(string Name, int Version, string Description, string StartStep,
    IReadOnlyList<string> Inputs, IReadOnlyList<WorkflowStepResponse> Steps);

public sealed record WorkflowStepResponse(string Id, string Kind, IReadOnlyList<string> Next);

/// <summary>Maps domain aggregates to transport DTOs (keeps endpoints thin and stable).</summary>
public static class Mapping
{
    public static RunResponse ToResponse(this WorkflowRun run) => new(
        run.Id, run.WorkflowName, run.WorkflowVersion, run.Status.ToString(), run.Outcome?.ToString(),
        run.CurrentStepId, run.ResultMessage ?? run.Error, run.TokensUsed, run.CostUsed, run.ToolCalls,
        run.ModelCalls, run.TenantId, run.CorrelationId, run.CreatedAt, run.CompletedAt);

    public static TraceEventResponse ToResponse(this TraceEvent e) => new(
        e.Ordinal, e.Type.ToString(), e.StepId, e.Timestamp, e.DurationMs, e.PromptTokens,
        e.CompletionTokens, e.Cost, e.PromptVersion, e.ToolName, e.Success, SafeParse(e.DataJson));

    public static ApprovalResponse ToResponse(this ApprovalTask a) => new(
        a.Id, a.RunId, a.StepId, a.TenantId, a.Title, a.Status.ToString(), a.RiskLevel.ToString(),
        SafeParse(a.ProposedActionJson), a.ReasoningTrace, a.RequestedAt, a.ExpiresAt, a.DecidedAt, a.DecidedBy);

    public static ToolResponse ToResponse(this ToolDescriptor d) => new(
        d.Name, d.Version, d.Description, d.SideEffect.ToString(), d.RiskLevel.ToString(),
        d.RequiredScopes, d.RequiresApproval, d.IsNaturallyIdempotent, d.RateLimitPerMinute,
        d.CostWeight, SafeParse(d.ParameterSchema.ToJsonString()));

    public static WorkflowResponse ToResponse(this WorkflowDefinition w) => new(
        w.Name, w.Version, w.Description, w.StartStepId, w.InputVariables,
        w.Steps.Select(s => new WorkflowStepResponse(s.Id, s.Kind.ToString(), s.Successors().ToList())).ToList());

    public static IReadOnlyDictionary<string, JsonNode?> ToInputs(this JsonObject? inputs)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (inputs is null) return result;
        foreach (var (key, value) in inputs) result[key] = value?.DeepClone();
        return result;
    }

    private static JsonNode? SafeParse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch { return JsonValue.Create(json); }
    }
}
