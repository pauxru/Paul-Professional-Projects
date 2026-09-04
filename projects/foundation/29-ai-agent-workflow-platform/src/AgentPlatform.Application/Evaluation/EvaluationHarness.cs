using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentPlatform.Application.Abstractions;
using AgentPlatform.Application.Engine;
using AgentPlatform.Domain.Abstractions;
using AgentPlatform.Domain.Runs;
using AgentPlatform.Domain.Tools;
using AgentPlatform.Domain.Tracing;
using AgentPlatform.Domain.Workflows;
using TraceEventType = AgentPlatform.Domain.Tracing.TraceEventType;

namespace AgentPlatform.Application.Evaluation;

/// <summary>
/// Runs the seeded evaluation scenarios against the deterministic mock model and scores each on task
/// success, tool-selection accuracy, unauthorised-attempt handling, budget adherence, approval
/// correctness, and measured latency/cost. Latency is real wall-clock; tokens and cost are
/// deterministic (from the trace), so the numbers are reproducible offline.
/// </summary>
public sealed class EvaluationHarness
{
    private static readonly HashSet<string> UnauthorisedCodes = new(StringComparer.Ordinal)
    {
        ToolErrorCodes.Unauthorized, ToolErrorCodes.PolicyViolation,
    };

    private readonly WorkflowEngine _engine;
    private readonly IRunStore _runs;
    private readonly IApprovalStore _approvals;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IEvalScenarioProvider _scenarios;

    public EvaluationHarness(WorkflowEngine engine, IRunStore runs, IApprovalStore approvals,
        IUnitOfWork unitOfWork, IClock clock, IEvalScenarioProvider scenarios)
    {
        _engine = engine;
        _runs = runs;
        _approvals = approvals;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _scenarios = scenarios;
    }

    public async Task<EvalReport> RunAsync(string? workflowFilter, CancellationToken cancellationToken)
    {
        var results = new List<ScenarioResult>();
        foreach (var scenario in _scenarios.GetScenarios())
        {
            if (workflowFilter is not null &&
                !string.Equals(scenario.WorkflowName, workflowFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            results.Add(await RunScenarioAsync(scenario, cancellationToken));
        }
        return Aggregate(results);
    }

    private async Task<ScenarioResult> RunScenarioAsync(EvalScenario scenario, CancellationToken cancellationToken)
    {
        var caller = new AgentCaller("eval-user", scenario.TenantId,
            new HashSet<string>(scenario.Scopes, StringComparer.Ordinal));
        var inputs = ParseInputs(scenario.InputJson);
        var command = new StartRunCommand
        {
            WorkflowName = scenario.WorkflowName,
            Version = scenario.WorkflowVersion,
            Inputs = inputs,
            CorrelationId = "eval-" + scenario.Id,
        };

        var stopwatch = Stopwatch.StartNew();
        RunResult result;
        try
        {
            result = await _engine.StartRunAsync(caller, command, cancellationToken);
            if (result.IsPaused && scenario.ExpectsApproval)
            {
                var approval = await _approvals.GetPendingForRunAsync(result.RunId, cancellationToken);
                if (approval is not null)
                {
                    if (scenario.ApproveWhenPaused)
                        approval.Approve("eval:auto", "auto-approved by eval harness", _clock.UtcNow);
                    else
                        approval.Reject("eval:auto", "auto-rejected by eval harness", _clock.UtcNow);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    result = await _engine.ContinueAfterApprovalAsync(result.RunId, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ScenarioResult
            {
                ScenarioId = scenario.Id,
                WorkflowName = scenario.WorkflowName,
                Category = scenario.Category,
                RunId = string.Empty,
                ExpectedOutcome = scenario.ExpectedOutcome.ToString(),
                ActualOutcome = "Exception",
                LatencyMs = stopwatch.ElapsedMilliseconds,
                Notes = ex.Message,
            };
        }
        stopwatch.Stop();

        var run = await _runs.GetAsync(result.RunId, cancellationToken);
        var trace = await _runs.GetTraceAsync(result.RunId, cancellationToken);
        return Score(scenario, run!, trace, stopwatch.ElapsedMilliseconds);
    }

    private static ScenarioResult Score(EvalScenario scenario, WorkflowRun run,
        IReadOnlyList<TraceEvent> trace, long latencyMs)
    {
        var toolEvents = trace.Where(t => t.Type == TraceEventType.ToolCall).ToList();
        var successfulTools = toolEvents.Where(t => t.Success && t.ToolName is not null)
            .Select(t => t.ToolName!).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var unauthorisedBlocks = toolEvents.Count(t => !t.Success && UnauthorisedCodes.Contains(ErrorCodeOf(t)));
        var hadApproval = trace.Any(t => t.Type == TraceEventType.ApprovalRequested);
        var haltedByGuardrail = run.Status == RunState.Halted;

        // Task success.
        double taskSuccess;
        if (scenario.ExpectsBudgetHalt)
            taskSuccess = haltedByGuardrail ? 1.0 : 0.0;
        else
            taskSuccess = run.Outcome == scenario.ExpectedOutcome ? 1.0 : 0.0;

        // Tool-selection accuracy (Jaccard over the expected set).
        double toolAccuracy;
        if (scenario.ExpectedToolsUsed.Count == 0)
        {
            toolAccuracy = 1.0;
        }
        else
        {
            var expected = scenario.ExpectedToolsUsed.ToHashSet(StringComparer.Ordinal);
            var intersection = expected.Count(successfulTools.Contains);
            var union = expected.Union(successfulTools, StringComparer.Ordinal).Count();
            toolAccuracy = union == 0 ? 1.0 : (double)intersection / union;
        }

        // Unauthorised handling.
        double unauthorisedHandling = scenario.ExpectsUnauthorisedBlock
            ? (unauthorisedBlocks > 0 ? 1.0 : 0.0)
            : (unauthorisedBlocks == 0 ? 1.0 : 0.0);

        // Budget adherence.
        double budgetAdherence = scenario.ExpectsBudgetHalt
            ? (haltedByGuardrail ? 1.0 : 0.0)
            : (haltedByGuardrail ? 0.0 : 1.0);

        // Approval correctness.
        double approvalCorrectness = hadApproval == scenario.ExpectsApproval ? 1.0 : 0.0;

        var tokens = trace.Where(t => t.Type == TraceEventType.ModelCall)
            .Sum(t => (long)(t.PromptTokens + t.CompletionTokens));
        var cost = trace.Sum(t => t.Cost);

        return new ScenarioResult
        {
            ScenarioId = scenario.Id,
            WorkflowName = scenario.WorkflowName,
            Category = scenario.Category,
            RunId = run.Id,
            TaskSuccess = taskSuccess,
            ToolSelectionAccuracy = toolAccuracy,
            UnauthorisedHandling = unauthorisedHandling,
            BudgetAdherence = budgetAdherence,
            ApprovalCorrectness = approvalCorrectness,
            UnauthorisedAttemptsBlocked = unauthorisedBlocks,
            TokensUsed = tokens,
            CostUsd = cost,
            LatencyMs = latencyMs,
            ActualOutcome = run.Outcome?.ToString() ?? run.Status.ToString(),
            ExpectedOutcome = scenario.ExpectedOutcome.ToString(),
        };
    }

    private static EvalReport Aggregate(IReadOnlyList<ScenarioResult> results)
    {
        if (results.Count == 0)
        {
            return new EvalReport { GeneratedAt = DateTimeOffset.UtcNow };
        }

        var byWorkflow = results
            .GroupBy(r => r.WorkflowName, StringComparer.Ordinal)
            .Select(g => new WorkflowScore
            {
                WorkflowName = g.Key,
                ScenarioCount = g.Count(),
                Passed = g.Count(r => r.Passed),
                TaskSuccess = g.Average(r => r.TaskSuccess),
                ToolSelectionAccuracy = g.Average(r => r.ToolSelectionAccuracy),
                UnauthorisedHandling = g.Average(r => r.UnauthorisedHandling),
                BudgetAdherence = g.Average(r => r.BudgetAdherence),
                ApprovalCorrectness = g.Average(r => r.ApprovalCorrectness),
                UnauthorisedAttemptsBlocked = g.Sum(r => r.UnauthorisedAttemptsBlocked),
                TotalTokens = g.Sum(r => r.TokensUsed),
                TotalCostUsd = g.Sum(r => r.CostUsd),
                MeanLatencyMs = g.Average(r => r.LatencyMs),
            })
            .OrderBy(w => w.WorkflowName, StringComparer.Ordinal)
            .ToList();

        return new EvalReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            TotalScenarios = results.Count,
            TotalPassed = results.Count(r => r.Passed),
            OverallTaskSuccess = results.Average(r => r.TaskSuccess),
            OverallToolSelectionAccuracy = results.Average(r => r.ToolSelectionAccuracy),
            OverallUnauthorisedHandling = results.Average(r => r.UnauthorisedHandling),
            OverallBudgetAdherence = results.Average(r => r.BudgetAdherence),
            OverallApprovalCorrectness = results.Average(r => r.ApprovalCorrectness),
            TotalUnauthorisedAttemptsBlocked = results.Sum(r => r.UnauthorisedAttemptsBlocked),
            TotalTokens = results.Sum(r => r.TokensUsed),
            TotalCostUsd = results.Sum(r => r.CostUsd),
            MeanLatencyMs = results.Average(r => r.LatencyMs),
            Workflows = byWorkflow,
            Scenarios = results,
        };
    }

    /// <summary>Compares a report against a baseline; regresses if any headline metric drops materially.</summary>
    public static RegressionGateResult CompareToBaseline(EvalReport report, EvalBaseline baseline, double tolerance = 0.02)
    {
        var regressions = new List<string>();
        void Check(string name, double current, double baselineValue)
        {
            if (current < baselineValue - tolerance)
                regressions.Add($"{name} regressed: {current:P1} < baseline {baselineValue:P1} (tolerance {tolerance:P0}).");
        }

        Check("Task success", report.OverallTaskSuccess, baseline.OverallTaskSuccess);
        Check("Tool-selection accuracy", report.OverallToolSelectionAccuracy, baseline.OverallToolSelectionAccuracy);
        Check("Unauthorised handling", report.OverallUnauthorisedHandling, baseline.OverallUnauthorisedHandling);
        Check("Approval correctness", report.OverallApprovalCorrectness, baseline.OverallApprovalCorrectness);
        Check("Budget adherence", report.OverallBudgetAdherence, baseline.OverallBudgetAdherence);

        return new RegressionGateResult { Passed = regressions.Count == 0, Regressions = regressions };
    }

    private static string ErrorCodeOf(TraceEvent traceEvent)
    {
        try
        {
            var node = JsonNode.Parse(traceEvent.DataJson);
            return node?["errorCode"]?.GetValue<string>() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IReadOnlyDictionary<string, JsonNode?> ParseInputs(string json)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (JsonNode.Parse(json) is JsonObject obj)
        {
            foreach (var (key, value) in obj)
                result[key] = value?.DeepClone();
        }
        return result;
    }
}
