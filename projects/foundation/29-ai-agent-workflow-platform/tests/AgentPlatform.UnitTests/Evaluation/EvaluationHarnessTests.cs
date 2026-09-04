using AgentPlatform.Application.Evaluation;
using AgentPlatform.Infrastructure.Catalog;
using AgentPlatform.UnitTests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.UnitTests.Evaluation;

/// <summary>
/// Proves the evaluation harness scores the seeded scenarios and that the regression gate detects a
/// material drop against a stored baseline. Numbers here are real (measured from the trace).
/// </summary>
public sealed class EvaluationHarnessTests
{
    private static async Task<EvalReport> RunAsync(AgentTestHost host, string? filter)
    {
        using var scope = host.Services.CreateScope();
        var harness = scope.ServiceProvider.GetRequiredService<EvaluationHarness>();
        return await harness.RunAsync(filter, default);
    }

    [Fact]
    public async Task Triage_suite_has_at_least_25_scenarios_and_passes()
    {
        using var host = new AgentTestHost();
        var report = await RunAsync(host, WorkflowCatalog.Triage);

        Assert.True(report.TotalScenarios >= 25, $"only {report.TotalScenarios} scenarios");
        Assert.True(report.OverallTaskSuccess >= 0.95, $"task success {report.OverallTaskSuccess:P1}");
        Assert.True(report.OverallUnauthorisedHandling >= 0.95, $"unauthorised handling {report.OverallUnauthorisedHandling:P1}");
    }

    [Fact]
    public async Task Full_suite_scores_all_three_workflows()
    {
        using var host = new AgentTestHost();
        var report = await RunAsync(host, null);

        Assert.True(report.TotalScenarios >= 75, $"only {report.TotalScenarios} scenarios");
        Assert.Equal(3, report.Workflows.Count);
        Assert.True(report.OverallTaskSuccess >= 0.95, $"task success {report.OverallTaskSuccess:P1}");
    }

    [Fact]
    public async Task Regression_gate_passes_against_matching_baseline()
    {
        using var host = new AgentTestHost();
        var report = await RunAsync(host, WorkflowCatalog.Triage);

        var baseline = new EvalBaseline
        {
            OverallTaskSuccess = report.OverallTaskSuccess,
            OverallToolSelectionAccuracy = report.OverallToolSelectionAccuracy,
            OverallUnauthorisedHandling = report.OverallUnauthorisedHandling,
            OverallApprovalCorrectness = report.OverallApprovalCorrectness,
            OverallBudgetAdherence = report.OverallBudgetAdherence,
        };

        var gate = EvaluationHarness.CompareToBaseline(report, baseline);
        Assert.True(gate.Passed, string.Join("; ", gate.Regressions));
    }

    [Fact]
    public async Task Regression_gate_fails_when_a_metric_drops()
    {
        using var host = new AgentTestHost();
        var report = await RunAsync(host, WorkflowCatalog.Triage);

        var inflated = new EvalBaseline
        {
            OverallTaskSuccess = report.OverallTaskSuccess + 0.1,
            OverallToolSelectionAccuracy = report.OverallToolSelectionAccuracy,
            OverallUnauthorisedHandling = report.OverallUnauthorisedHandling,
            OverallApprovalCorrectness = report.OverallApprovalCorrectness,
            OverallBudgetAdherence = report.OverallBudgetAdherence,
        };

        var gate = EvaluationHarness.CompareToBaseline(report, inflated);
        Assert.False(gate.Passed);
        Assert.NotEmpty(gate.Regressions);
    }
}
