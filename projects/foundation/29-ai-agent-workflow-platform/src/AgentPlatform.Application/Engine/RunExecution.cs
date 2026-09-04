using System.Text.Json.Nodes;
using AgentPlatform.Application.Abstractions;
using AgentPlatform.Domain.Budgets;
using AgentPlatform.Domain.Workflows;

namespace AgentPlatform.Application.Engine;

/// <summary>Mutable per-step execution context: the live state bag and budget for one attempt.</summary>
internal sealed class RunExecution
{
    public RunExecution(Domain.Runs.WorkflowRun run, WorkflowDefinition workflow, AgentCaller caller,
        JsonObject state, BudgetTracker budget, BudgetLimits limits)
    {
        Run = run;
        Workflow = workflow;
        Caller = caller;
        State = state;
        Budget = budget;
        Limits = limits;
    }

    public Domain.Runs.WorkflowRun Run { get; }
    public WorkflowDefinition Workflow { get; }
    public AgentCaller Caller { get; }
    public JsonObject State { get; }
    public BudgetTracker Budget { get; }
    public BudgetLimits Limits { get; }
}
