using AgentPlatform.Domain.Budgets;
using AgentPlatform.Domain.Models;

namespace AgentPlatform.Application.Engine;

/// <summary>Tunable engine behaviour. All time-based values honour <see cref="Abstractions.IDelayStrategy"/>.</summary>
public sealed record EngineOptions
{
    /// <summary>Attempts per step before a transient failure becomes terminal.</summary>
    public int MaxStepAttempts { get; init; } = 3;

    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ModelCallTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Global cap on top-level step transitions, a final backstop against runaway runs.</summary>
    public int MaxStepTransitions { get; init; } = 200;
}

/// <summary>Simple linear token pricing so cost is deterministic and reproducible in evals.</summary>
public sealed record ModelPricing(decimal PromptCostPer1K, decimal CompletionCostPer1K)
{
    public static ModelPricing Default { get; } = new(0.0015m, 0.0020m);

    public decimal Cost(TokenUsage usage) =>
        Math.Round(usage.PromptTokens / 1000m * PromptCostPer1K + usage.CompletionTokens / 1000m * CompletionCostPer1K, 6);
}

/// <summary>Command to start a run.</summary>
public sealed record StartRunCommand
{
    public required string WorkflowName { get; init; }
    public int? Version { get; init; }
    public required IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> Inputs { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? CorrelationId { get; init; }
    public BudgetLimits? BudgetOverride { get; init; }
}

/// <summary>Outcome of executing (or resuming) a run to its next stopping point.</summary>
public sealed record RunResult(string RunId, Domain.Runs.RunState State, Domain.Workflows.WorkflowOutcome? Outcome, string? Message)
{
    public bool IsPaused => State == Domain.Runs.RunState.WaitingForApproval;
    public bool IsTerminal => State is Domain.Runs.RunState.Completed or Domain.Runs.RunState.Failed
        or Domain.Runs.RunState.Cancelled or Domain.Runs.RunState.Halted;
}
