namespace AgentPlatform.Domain.Workflows;

/// <summary>
/// A declarative, versioned workflow: an entry step, a set of steps forming an acyclic top-level
/// graph, and the input variables a run must supply. Identity is (<see cref="Name"/>,
/// <see cref="Version"/>); a new version is a new immutable definition.
/// </summary>
public sealed record WorkflowDefinition
{
    public required string Name { get; init; }
    public required int Version { get; init; }
    public string Description { get; init; } = string.Empty;
    public required string StartStepId { get; init; }
    public required IReadOnlyList<WorkflowStep> Steps { get; init; }
    public IReadOnlyList<string> InputVariables { get; init; } = Array.Empty<string>();

    /// <summary>Default budget applied to runs that do not override it.</summary>
    public Budgets.BudgetLimits DefaultBudget { get; init; } = Budgets.BudgetLimits.Default;

    public string Key => $"{Name}@v{Version}";

    public WorkflowStep? FindStep(string id) => Steps.FirstOrDefault(s => s.Id == id);
}
