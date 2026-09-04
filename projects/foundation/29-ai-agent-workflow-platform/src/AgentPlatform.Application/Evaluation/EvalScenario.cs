using AgentPlatform.Domain.Workflows;

namespace AgentPlatform.Application.Evaluation;

/// <summary>Broad category of an evaluation scenario, used for reporting.</summary>
public enum EvalCategory
{
    Normal,
    Approval,
    Adversarial,
    Budget,
}

/// <summary>
/// One seeded evaluation scenario: a fixed input plus the expected outcome. Scenarios run against the
/// <c>DeterministicMockModel</c> so scoring is fully reproducible offline.
/// </summary>
public sealed record EvalScenario
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public EvalCategory Category { get; init; } = EvalCategory.Normal;

    public required string WorkflowName { get; init; }
    public required int WorkflowVersion { get; init; }
    public required string InputJson { get; init; }

    public string TenantId { get; init; } = "eval-tenant";
    public IReadOnlyList<string> Scopes { get; init; } = new[] { "agents:run" };

    /// <summary>The workflow outcome we expect the run to reach.</summary>
    public required WorkflowOutcome ExpectedOutcome { get; init; }

    /// <summary>Tool names we expect to be invoked (order-independent). Empty = no tool expectation.</summary>
    public IReadOnlyList<string> ExpectedToolsUsed { get; init; } = Array.Empty<string>();

    /// <summary>True if the run should pause for human approval.</summary>
    public bool ExpectsApproval { get; init; }

    /// <summary>If it pauses, whether the harness approves (true) or rejects (false).</summary>
    public bool ApproveWhenPaused { get; init; } = true;

    /// <summary>True if the scenario should trigger at least one blocked unauthorised/policy tool attempt.</summary>
    public bool ExpectsUnauthorisedBlock { get; init; }

    /// <summary>True if the scenario is expected to halt on a budget guardrail.</summary>
    public bool ExpectsBudgetHalt { get; init; }
}
