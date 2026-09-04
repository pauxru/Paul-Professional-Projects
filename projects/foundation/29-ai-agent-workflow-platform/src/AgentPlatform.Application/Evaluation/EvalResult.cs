namespace AgentPlatform.Application.Evaluation;

/// <summary>Per-scenario measured scores. Each score is in [0,1] unless noted.</summary>
public sealed record ScenarioResult
{
    public required string ScenarioId { get; init; }
    public required string WorkflowName { get; init; }
    public required EvalCategory Category { get; init; }
    public required string RunId { get; init; }

    public double TaskSuccess { get; init; }
    public double ToolSelectionAccuracy { get; init; }
    public double UnauthorisedHandling { get; init; }
    public double BudgetAdherence { get; init; }
    public double ApprovalCorrectness { get; init; }

    public int UnauthorisedAttemptsBlocked { get; init; }
    public long TokensUsed { get; init; }
    public decimal CostUsd { get; init; }
    public long LatencyMs { get; init; }

    public string ActualOutcome { get; init; } = string.Empty;
    public string ExpectedOutcome { get; init; } = string.Empty;
    public string? Notes { get; init; }

    /// <summary>A scenario passes when every applicable correctness metric is satisfied.</summary>
    public bool Passed => TaskSuccess >= 1.0
        && UnauthorisedHandling >= 1.0
        && BudgetAdherence >= 1.0
        && ApprovalCorrectness >= 1.0;
}

/// <summary>Aggregate scores for a single workflow.</summary>
public sealed record WorkflowScore
{
    public required string WorkflowName { get; init; }
    public int ScenarioCount { get; init; }
    public int Passed { get; init; }
    public double TaskSuccess { get; init; }
    public double ToolSelectionAccuracy { get; init; }
    public double UnauthorisedHandling { get; init; }
    public double BudgetAdherence { get; init; }
    public double ApprovalCorrectness { get; init; }
    public int UnauthorisedAttemptsBlocked { get; init; }
    public long TotalTokens { get; init; }
    public decimal TotalCostUsd { get; init; }
    public double MeanLatencyMs { get; init; }
}

/// <summary>Full evaluation report across all workflows.</summary>
public sealed record EvalReport
{
    public DateTimeOffset GeneratedAt { get; init; }
    public int TotalScenarios { get; init; }
    public int TotalPassed { get; init; }
    public double OverallTaskSuccess { get; init; }
    public double OverallToolSelectionAccuracy { get; init; }
    public double OverallUnauthorisedHandling { get; init; }
    public double OverallBudgetAdherence { get; init; }
    public double OverallApprovalCorrectness { get; init; }
    public int TotalUnauthorisedAttemptsBlocked { get; init; }
    public long TotalTokens { get; init; }
    public decimal TotalCostUsd { get; init; }
    public double MeanLatencyMs { get; init; }
    public IReadOnlyList<WorkflowScore> Workflows { get; init; } = Array.Empty<WorkflowScore>();
    public IReadOnlyList<ScenarioResult> Scenarios { get; init; } = Array.Empty<ScenarioResult>();
}

/// <summary>A stored baseline the regression gate compares new runs against.</summary>
public sealed record EvalBaseline
{
    public double OverallTaskSuccess { get; init; }
    public double OverallToolSelectionAccuracy { get; init; }
    public double OverallUnauthorisedHandling { get; init; }
    public double OverallApprovalCorrectness { get; init; }
    public double OverallBudgetAdherence { get; init; }
}

/// <summary>Result of comparing a report against a baseline.</summary>
public sealed record RegressionGateResult
{
    public bool Passed { get; init; }
    public IReadOnlyList<string> Regressions { get; init; } = Array.Empty<string>();
}
