using AgentPlatform.Domain.Budgets;
using AgentPlatform.Domain.Workflows;

namespace AgentPlatform.Application.Engine;

/// <summary>Discriminates how a single step execution concluded.</summary>
public enum StepResultKind
{
    Advanced,
    Paused,
    Terminal,
    Halted,
    TransientFailure,
    PermanentFailure,
}

/// <summary>Immutable outcome of executing one step, interpreted by the run loop.</summary>
public sealed record StepResult(
    StepResultKind Kind,
    string? NextStepId = null,
    WorkflowOutcome? Outcome = null,
    string? Message = null,
    BudgetHaltReason HaltReason = BudgetHaltReason.None,
    string? ErrorCode = null)
{
    public static StepResult Advance(string? next) => new(StepResultKind.Advanced, NextStepId: next);
    public static StepResult Pause() => new(StepResultKind.Paused);
    public static StepResult Terminate(WorkflowOutcome outcome, string? message) => new(StepResultKind.Terminal, Outcome: outcome, Message: message);
    public static StepResult Halt(BudgetHaltReason reason, string message) => new(StepResultKind.Halted, HaltReason: reason, Message: message);
    public static StepResult Transient(string message, string? code) => new(StepResultKind.TransientFailure, Message: message, ErrorCode: code);
    public static StepResult Permanent(string message, string? code) => new(StepResultKind.PermanentFailure, Message: message, ErrorCode: code);
}
