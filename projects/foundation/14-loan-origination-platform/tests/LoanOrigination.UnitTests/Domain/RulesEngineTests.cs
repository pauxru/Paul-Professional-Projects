using LoanOrigination.Application.Contracts;
using LoanOrigination.Application.Services;
using LoanOrigination.Domain.Models;
using LoanOrigination.Domain.Rules;

namespace LoanOrigination.UnitTests.Domain;

public sealed class RulesEngineTests
{
    private readonly DeclarativeRulesEngine _engine = new();

    [Fact]
    public void Evaluate_AllCombinator_WhenEveryConditionMatches_TriggersRule()
    {
        var ruleset = TestData.Ruleset(new RuleDefinition(
            "all-rule",
            1,
            1,
            ConditionDefinition.All(
                ConditionDefinition.Compare(nameof(ApplicantFacts.MonthlyNetIncome), ComparisonOperator.GreaterThan, 50_000m),
                ConditionDefinition.Compare(nameof(ApplicantFacts.Dependants), ComparisonOperator.LessThanOrEqual, 2)),
            new RuleOutcome(RuleOutcomeType.Pass),
            "Income and dependant policy passes."));

        var trace = _engine.Evaluate(ruleset, TestData.Facts());

        Assert.True(Assert.Single(trace.Rules).Matched);
        Assert.Equal(2, trace.Rules[0].InputsRead.Count);
    }

    [Fact]
    public void Evaluate_AnyCombinator_WhenOneConditionMatches_TriggersReferral()
    {
        var ruleset = TestData.Ruleset(new RuleDefinition(
            "any-rule",
            1,
            1,
            ConditionDefinition.Any(
                ConditionDefinition.Compare(nameof(ApplicantFacts.BureauGrade), ComparisonOperator.Equals, "E"),
                ConditionDefinition.Compare(nameof(ApplicantFacts.PastArrearsCount), ComparisonOperator.GreaterThan, 0)),
            new RuleOutcome(RuleOutcomeType.Refer),
            "Manual review trigger."));

        var trace = _engine.Evaluate(ruleset, TestData.Facts(arrears: 1));

        Assert.Equal(RuleDecision.Refer, trace.Decision);
        Assert.True(trace.Rules[0].Matched);
    }

    [Fact]
    public void Evaluate_NotCombinator_WhenNestedConditionDoesNotMatch_TriggersRule()
    {
        var ruleset = TestData.Ruleset(new RuleDefinition(
            "not-rule",
            1,
            1,
            ConditionDefinition.Not(ConditionDefinition.Compare(nameof(ApplicantFacts.BureauGrade), ComparisonOperator.Equals, "E")),
            new RuleOutcome(RuleOutcomeType.Pass),
            "Not grade E."));

        var trace = _engine.Evaluate(ruleset, TestData.Facts(grade: "B"));

        Assert.True(trace.Rules[0].Matched);
        Assert.Equal(RuleDecision.Pass, trace.Decision);
    }

    [Fact]
    public void Evaluate_FailOutcome_TakesPrecedenceOverReferral()
    {
        var ruleset = TestData.Ruleset(
            new RuleDefinition("refer", 1, 1, ConditionDefinition.Compare(nameof(ApplicantFacts.MonthlyNetIncome), ComparisonOperator.GreaterThan, 0m), new RuleOutcome(RuleOutcomeType.Refer), "Refer"),
            new RuleDefinition("fail", 1, 2, ConditionDefinition.Compare(nameof(ApplicantFacts.PastArrearsCount), ComparisonOperator.GreaterThanOrEqual, 3), new RuleOutcome(RuleOutcomeType.Fail), "Fail"));

        var trace = _engine.Evaluate(ruleset, TestData.Facts(arrears: 3));

        Assert.Equal(RuleDecision.Fail, trace.Decision);
        Assert.Equal(2, trace.Rules.Count);
    }

    [Fact]
    public void Evaluate_AdjustLimit_UsesMostConservativeTriggeredAmount()
    {
        var ruleset = TestData.Ruleset(
            new RuleDefinition("limit-one", 1, 1, ConditionDefinition.Compare(nameof(ApplicantFacts.MonthlyNetIncome), ComparisonOperator.GreaterThan, 0m), new RuleOutcome(RuleOutcomeType.AdjustLimit, 300_000m), "Limit 1"),
            new RuleDefinition("limit-two", 1, 2, ConditionDefinition.Compare(nameof(ApplicantFacts.Dependants), ComparisonOperator.GreaterThan, 0), new RuleOutcome(RuleOutcomeType.AdjustLimit, 200_000m), "Limit 2"));

        var trace = _engine.Evaluate(ruleset, TestData.Facts());

        Assert.Equal(200_000m, trace.AdjustedMaximumPrincipal);
    }

    [Fact]
    public void Evaluate_AdjustRate_SumsTriggeredRateAdjustments()
    {
        var ruleset = TestData.Ruleset(
            new RuleDefinition("rate-one", 1, 1, ConditionDefinition.Compare(nameof(ApplicantFacts.MonthlyNetIncome), ComparisonOperator.GreaterThan, 0m), new RuleOutcome(RuleOutcomeType.AdjustRate, 1.25m), "Rate 1"),
            new RuleDefinition("rate-two", 1, 2, ConditionDefinition.Compare(nameof(ApplicantFacts.BureauGrade), ComparisonOperator.In, new[] { "A", "B" }), new RuleOutcome(RuleOutcomeType.AdjustRate, 0.75m), "Rate 2"));

        var trace = _engine.Evaluate(ruleset, TestData.Facts(grade: "B"));

        Assert.Equal(2m, trace.RateAdjustmentPercentagePoints);
    }

    [Fact]
    public void Evaluate_RequireDocument_RefersAndReportsDocument()
    {
        var ruleset = TestData.Ruleset(new RuleDefinition(
            "income-proof",
            1,
            1,
            ConditionDefinition.Compare(nameof(ApplicantFacts.RequestedPrincipal), ComparisonOperator.GreaterThan, 50_000m),
            new RuleOutcome(RuleOutcomeType.RequireDocument, DocumentType: "Payslip"),
            "Large loan requires payslip."));

        var trace = _engine.Evaluate(ruleset, TestData.Facts());

        Assert.Equal(RuleDecision.Refer, trace.Decision);
        Assert.Contains("Payslip", trace.RequiredDocuments);
    }

    [Fact]
    public void Evaluate_StringContainsAndBooleanEquality_UsesTypedFacts()
    {
        var ruleset = TestData.Ruleset(new RuleDefinition(
            "typed-rule",
            1,
            1,
            ConditionDefinition.All(
                ConditionDefinition.Compare(nameof(ApplicantFacts.Currency), ComparisonOperator.Contains, "ke"),
                ConditionDefinition.Compare(nameof(ApplicantFacts.IsSme), ComparisonOperator.Equals, false)),
            new RuleOutcome(RuleOutcomeType.Pass),
            "Typed facts work."));

        var trace = _engine.Evaluate(ruleset, TestData.Facts());

        Assert.True(trace.Rules[0].Matched);
    }

    [Fact]
    public void Evaluate_SameInputsAndVersion_ProducesIdenticalTrace()
    {
        var ruleset = TestData.Ruleset(new RuleDefinition(
            "trace-rule",
            4,
            1,
            ConditionDefinition.Compare(nameof(ApplicantFacts.MonthlyNetIncome), ComparisonOperator.GreaterThan, 0m),
            new RuleOutcome(RuleOutcomeType.Pass),
            "Income is positive."));

        var first = _engine.Evaluate(ruleset, TestData.Facts());
        var second = _engine.Evaluate(ruleset, TestData.Facts());

        Assert.True(first.IsReproducibleWith(second));
        Assert.Equal("trace-rule", Assert.Single(first.Rules).RuleId);
        Assert.NotEmpty(first.Rules[0].InputsRead);
    }

    [Fact]
    public void Evaluate_RulesetVersioning_OldApplicationRetainsOriginalDecision()
    {
        var v1 = new RuleSetDefinition("versioned", 1, "v1", TestData.Now,
            [new RuleDefinition("v1-pass", 1, 1, ConditionDefinition.Compare(nameof(ApplicantFacts.MonthlyNetIncome), ComparisonOperator.GreaterThan, 0m), new RuleOutcome(RuleOutcomeType.Pass), "Pass")]);
        var v2 = new RuleSetDefinition("versioned", 2, "v2", TestData.Now,
            [new RuleDefinition("v2-fail", 1, 1, ConditionDefinition.Compare(nameof(ApplicantFacts.MonthlyNetIncome), ComparisonOperator.GreaterThan, 0m), new RuleOutcome(RuleOutcomeType.Fail), "Fail")]);
        var historical = _engine.Evaluate(v1, TestData.Facts());
        var application = TestData.Application() with { RulesetId = v1.Id, RulesetVersion = v1.Version, DecisionTrace = historical };

        var laterDecision = _engine.Evaluate(v2, application.Facts);

        Assert.Equal(RuleDecision.Pass, application.DecisionTrace!.Decision);
        Assert.Equal(1, application.RulesetVersion);
        Assert.Equal(RuleDecision.Fail, laterDecision.Decision);
    }

    [Fact]
    public async Task SimulateAsync_CandidateRuleset_ReportsDecisionFlip()
    {
        var repository = new InMemoryLoanRepository();
        var audit = new CollectingAuditWriter();
        var oldRuleset = TestData.Ruleset(new RuleDefinition("old", 1, 1, ConditionDefinition.Compare(nameof(ApplicantFacts.MonthlyNetIncome), ComparisonOperator.GreaterThan, 0m), new RuleOutcome(RuleOutcomeType.Pass), "Pass"));
        var historicalTrace = _engine.Evaluate(oldRuleset, TestData.Facts());
        var application = TestData.Application() with { DecisionTrace = historicalTrace };
        await repository.AddApplicationAsync(application, CancellationToken.None);
        var service = new RulesetService(repository, _engine, new FakeClock(TestData.Now), audit);
        var candidate = new RuleSetDefinition("candidate", 2, "Candidate", TestData.Now,
            [new RuleDefinition("new", 1, 1, ConditionDefinition.Compare(nameof(ApplicantFacts.MonthlyNetIncome), ComparisonOperator.GreaterThan, 0m), new RuleOutcome(RuleOutcomeType.Fail), "Fail")]);

        var report = await service.SimulateAsync(new WhatIfRequest(candidate), CancellationToken.None);

        Assert.Equal(1, report.EvaluatedApplications);
        Assert.Equal(1, report.FlippedApplications);
        Assert.True(Assert.Single(report.Deltas).Flipped);
    }
}
