using System.Text.Json.Nodes;

namespace AgentPlatform.Domain.Workflows;

/// <summary>
/// Base type for a declarative, typed workflow step. Steps are pure data; the execution engine
/// interprets them. Every step has an <see cref="Id"/> unique within its workflow and, for most
/// kinds, a <see cref="Next"/> pointer that forms the (acyclic) top-level graph. Bounded
/// iteration is expressed only through <see cref="LoopStep"/>.
/// </summary>
public abstract record WorkflowStep
{
    public required string Id { get; init; }
    public string? Description { get; init; }
    public abstract StepKind Kind { get; }

    /// <summary>Ids of the steps this step can transition to (for graph validation).</summary>
    public abstract IEnumerable<string> Successors();
}

/// <summary>Runs a model with a bounded tool-calling loop, then stores the final answer.</summary>
public sealed record ModelStep : WorkflowStep
{
    public override StepKind Kind => StepKind.Model;

    /// <summary>Prompt template rendered as the system message.</summary>
    public required string PromptTemplateId { get; init; }

    /// <summary>State variable providing the user message text.</summary>
    public string? InputVariable { get; init; }

    /// <summary>Names of the tools the model may call in this step (a per-step allow-list).</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

    /// <summary>Hard cap on model/tool iterations, defending against loops.</summary>
    public int MaxIterations { get; init; } = 6;

    /// <summary>State variable that receives the final assistant text.</summary>
    public required string OutputVariable { get; init; }

    public string? Next { get; init; }

    public override IEnumerable<string> Successors() => Next is null ? [] : [Next];
}

/// <summary>Calls a single tool with arguments bound deterministically from run state.</summary>
public sealed record ToolStep : WorkflowStep
{
    public override StepKind Kind => StepKind.Tool;

    public required string ToolName { get; init; }

    /// <summary>
    /// Template object for the arguments. String values beginning with '$' reference a state
    /// variable; all other JSON is a literal. Resolved and schema-validated before execution.
    /// </summary>
    public required JsonObject ArgumentsTemplate { get; init; }

    public required string OutputVariable { get; init; }

    public string? Next { get; init; }

    public override IEnumerable<string> Successors() => Next is null ? [] : [Next];

    // Records use structural equality; JsonObject is reference-typed, so override to keep
    // definitions comparable by id (sufficient for our registry semantics).
    public bool Equals(ToolStep? other) => other is not null && other.Id == Id;
    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>Deterministic branch on a single state variable — no model involved.</summary>
public sealed record ConditionStep : WorkflowStep
{
    public override StepKind Kind => StepKind.Condition;

    public required string Variable { get; init; }
    public required ConditionOperator Operator { get; init; }
    public JsonNode? Value { get; init; }
    public required string WhenTrue { get; init; }
    public required string WhenFalse { get; init; }

    public override IEnumerable<string> Successors() => [WhenTrue, WhenFalse];

    public bool Equals(ConditionStep? other) => other is not null && other.Id == Id;
    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>Runs several inline branches concurrently, joining before <see cref="Next"/>.</summary>
public sealed record ParallelStep : WorkflowStep
{
    public override StepKind Kind => StepKind.Parallel;

    public required IReadOnlyList<WorkflowStep> Branches { get; init; }
    public string? Next { get; init; }

    public override IEnumerable<string> Successors() => Next is null ? [] : [Next];
}

/// <summary>Repeats an inline body up to a hard cap while a continue-condition holds.</summary>
public sealed record LoopStep : WorkflowStep
{
    public override StepKind Kind => StepKind.Loop;

    /// <summary>Inline body executed each iteration (typically Tool/Transform/Model steps).</summary>
    public required IReadOnlyList<WorkflowStep> Body { get; init; }

    /// <summary>Hard iteration cap. Never unbounded.</summary>
    public required int MaxIterations { get; init; }

    /// <summary>Optional state variable holding a collection to iterate; each item bound to ItemVariable.</summary>
    public string? OverVariable { get; init; }
    public string? ItemVariable { get; init; }

    public string? Next { get; init; }

    public override IEnumerable<string> Successors() => Next is null ? [] : [Next];
}

/// <summary>Pauses the run for human approval of a proposed (typically mutating) action.</summary>
public sealed record HumanApprovalStep : WorkflowStep
{
    public override StepKind Kind => StepKind.HumanApproval;

    /// <summary>State variable holding the proposed action payload shown to the approver.</summary>
    public required string ProposedActionVariable { get; init; }
    public string ActionTitle { get; init; } = "Approve action";
    public int TimeoutSeconds { get; init; } = 3600;
    public ApprovalDefault DefaultOnTimeout { get; init; } = ApprovalDefault.Reject;

    public required string OnApprove { get; init; }
    public required string OnReject { get; init; }

    public override IEnumerable<string> Successors() => [OnApprove, OnReject];
}

/// <summary>Applies a registered deterministic transform to run state.</summary>
public sealed record TransformStep : WorkflowStep
{
    public override StepKind Kind => StepKind.Transform;

    public required string TransformId { get; init; }
    public required string OutputVariable { get; init; }
    public string? Next { get; init; }

    public override IEnumerable<string> Successors() => Next is null ? [] : [Next];
}

/// <summary>Ends the run with a declared outcome.</summary>
public sealed record TerminalStep : WorkflowStep
{
    public override StepKind Kind => StepKind.Terminal;

    public required WorkflowOutcome Outcome { get; init; }
    public string? MessageVariable { get; init; }

    public override IEnumerable<string> Successors() => [];
}
