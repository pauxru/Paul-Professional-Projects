namespace AgentPlatform.Application.Abstractions;

/// <summary>
/// Business/operational metrics port. Implemented over an OpenTelemetry <c>Meter</c> in the API,
/// and as a no-op or counting double in tests.
/// </summary>
public interface IAgentMetrics
{
    void RunStarted(string workflow);
    void RunCompleted(string workflow, string outcome, double durationSeconds);
    void StepExecuted(string workflow, string stepKind, double durationMs);
    void ToolInvoked(string tool, bool success);
    void ModelCalled(string model, int tokens);
    void ApprovalRequested(string workflow);
    void ApprovalDecided(string workflow, string decision, double latencySeconds);
    void BudgetHalt(string workflow, string reason);
}

/// <summary>Metrics sink that ignores everything (safe default).</summary>
public sealed class NullAgentMetrics : IAgentMetrics
{
    public void RunStarted(string workflow) { }
    public void RunCompleted(string workflow, string outcome, double durationSeconds) { }
    public void StepExecuted(string workflow, string stepKind, double durationMs) { }
    public void ToolInvoked(string tool, bool success) { }
    public void ModelCalled(string model, int tokens) { }
    public void ApprovalRequested(string workflow) { }
    public void ApprovalDecided(string workflow, string decision, double latencySeconds) { }
    public void BudgetHalt(string workflow, string reason) { }
}
