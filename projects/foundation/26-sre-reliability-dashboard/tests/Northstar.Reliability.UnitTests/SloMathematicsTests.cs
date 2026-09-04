using Northstar.Reliability.Application.Simulation;
using Northstar.Reliability.Domain.Common;
using Northstar.Reliability.Domain.Incidents;
using Northstar.Reliability.Domain.Postmortems;
using Northstar.Reliability.Domain.Reporting;
using Northstar.Reliability.Domain.Services;
using Northstar.Reliability.Domain.Slo;
using Northstar.Reliability.Domain.Telemetry;
using Northstar.Reliability.UnitTests.Fixtures;

namespace Northstar.Reliability.UnitTests;

public sealed class SloMathematicsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RequestBasedAvailability_WithHandBuiltEvents_ComputesGoodOverValid()
    {
        var result = SliEvaluator.Evaluate(Availability(), [Metric(Now, 100, 3), Metric(Now.AddMinutes(-1), 200, 2)], Now.AddHours(-1), Now.AddMinutes(1));

        Assert.Equal(295, result.GoodEvents);
        Assert.Equal(300, result.ValidEvents);
        Assert.Equal(5, result.BadEvents);
        Assert.Equal(295m / 300m, result.Value);
    }

    [Fact]
    public void RequestBasedSli_WithEndpointFilter_ExcludesOtherEndpoints()
    {
        var filtered = SliDefinition.Create("checkout", "checkout", SliAggregationMode.RequestBased, SliKind.Availability, new SliFilter("/checkout", null, null), null, Now);
        var result = SliEvaluator.Evaluate(filtered, [Metric(Now, 100, 1, endpoint: "/checkout"), Metric(Now, 500, 50, endpoint: "/health")], Now.AddHours(-1), Now.AddMinutes(1));

        Assert.Equal(99, result.GoodEvents);
        Assert.Equal(100, result.ValidEvents);
    }

    [Fact]
    public void RequestBasedLatency_WithHistogram_UsesDeclaredThreshold()
    {
        var sli = SliDefinition.Create("latency", "checkout", SliAggregationMode.RequestBased, SliKind.Latency, SliFilter.All, 300m, Now);
        var metric = Metric(Now, 100, 0, histogram:
        [
            new LatencyHistogramBucket(100m, 50),
            new LatencyHistogramBucket(300m, 40),
            new LatencyHistogramBucket(1000m, 10)
        ]);

        var result = SliEvaluator.Evaluate(sli, [metric], Now.AddHours(-1), Now.AddMinutes(1));

        Assert.Equal(90, result.GoodEvents);
        Assert.Equal(100, result.ValidEvents);
    }

    [Fact]
    public void RequestBasedQuality_ComputesCorrectnessEvents()
    {
        var sli = SliDefinition.Create("quality", "checkout", SliAggregationMode.RequestBased, SliKind.Quality, SliFilter.All, null, Now);
        var result = SliEvaluator.Evaluate(sli, [Metric(Now, 100, 0, qualityGood: 97, qualityValid: 100)], Now.AddHours(-1), Now.AddMinutes(1));

        Assert.Equal(97, result.GoodEvents);
        Assert.Equal(100, result.ValidEvents);
    }

    [Fact]
    public void RequestBasedFreshness_ComputesFreshEvents()
    {
        var sli = SliDefinition.Create("freshness", "checkout", SliAggregationMode.RequestBased, SliKind.Freshness, SliFilter.All, null, Now);
        var result = SliEvaluator.Evaluate(sli, [Metric(Now, 100, 0, freshnessGood: 88, freshnessValid: 100)], Now.AddHours(-1), Now.AddMinutes(1));

        Assert.Equal(88, result.GoodEvents);
        Assert.Equal(100, result.ValidEvents);
    }

    [Fact]
    public void WindowBasedAvailability_WithProbeData_CountsGoodMinutes()
    {
        var result = SliEvaluator.Evaluate(WindowAvailability(),
        [
            Metric(Now.AddMinutes(-2), 100, 0, probeGood: 1, probeTotal: 1),
            Metric(Now.AddMinutes(-1), 100, 3, probeGood: 0, probeTotal: 1),
            Metric(Now, 100, 0, probeGood: 1, probeTotal: 1)
        ], Now.AddHours(-1), Now.AddMinutes(1));

        Assert.Equal(2, result.GoodEvents);
        Assert.Equal(3, result.ValidEvents);
        Assert.Equal(3, result.EvaluatedWindows);
    }

    [Fact]
    public void WindowBasedLatency_WithOneBadRequest_MarksWholeMinuteBad()
    {
        var sli = SliDefinition.Create("latency", "checkout", SliAggregationMode.WindowBased, SliKind.Latency, SliFilter.All, 300m, Now);
        var result = SliEvaluator.Evaluate(sli,
        [
            Metric(Now.AddMinutes(-1), 100, 0, latencyGood: 99),
            Metric(Now, 100, 0, latencyGood: 100)
        ], Now.AddHours(-1), Now.AddMinutes(1));

        Assert.Equal(1, result.GoodEvents);
        Assert.Equal(2, result.ValidEvents);
    }

    [Fact]
    public void SliEvaluation_StartIsInclusiveAndEndIsExclusive()
    {
        var result = SliEvaluator.Evaluate(Availability(),
        [
            Metric(Now.AddHours(-1), 100, 1),
            Metric(Now, 100, 80)
        ], Now.AddHours(-1), Now);

        Assert.Equal(99, result.GoodEvents);
        Assert.Equal(100, result.ValidEvents);
    }

    [Fact]
    public void RollingWindow_UsesExactDurationAcrossMonthBoundary()
    {
        var slo = Slo(0.999m, SloWindowKind.Rolling, rollingDays: 28);
        var clock = new FakeClock(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        var window = slo.ResolveWindow(clock.UtcNow);

        Assert.Equal(new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero), window.Start);
        Assert.Equal(TimeSpan.FromDays(28), window.CompliancePeriod);
    }

    [Fact]
    public void CalendarMonthlyWindow_ResetsAtMonthRollover()
    {
        var slo = Slo(0.999m, SloWindowKind.Calendar);
        var clock = new FakeClock(new DateTimeOffset(2026, 2, 1, 0, 5, 0, TimeSpan.Zero));

        var window = slo.ResolveWindow(clock.UtcNow);

        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), window.Start);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), window.CalendarPeriodEnd);
    }

    [Fact]
    public void CalendarQuarterlyWindow_StartsAtQuarterBoundary()
    {
        var slo = Slo(0.999m, SloWindowKind.Calendar, period: CalendarWindowPeriod.Quarterly);

        var window = slo.ResolveWindow(new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), window.Start);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), window.CalendarPeriodEnd);
    }

    [Fact]
    public void ErrorBudget_For99Point9AndTenThousandEvents_IsTenEvents()
    {
        var budget = ErrorBudgetCalculator.Calculate(0.999m, new SliResult(9_990, 10_000, 0));

        Assert.Equal(10m, budget.Total);
        Assert.Equal(10m, budget.Consumed);
        Assert.Equal(0m, budget.Remaining);
        Assert.Equal(100m, budget.ConsumedPercent);
    }

    [Fact]
    public void ErrorBudget_WithNoValidEvents_IsUntouched()
    {
        var budget = ErrorBudgetCalculator.Calculate(0.999m, new SliResult(0, 0, 0));

        Assert.Equal(100m, budget.RemainingPercent);
        Assert.Equal(0m, budget.Consumed);
    }

    [Fact]
    public void BurnRate_OneHourFixture_Is14Point4Times()
    {
        var burn = BurnRateCalculator.Calculate(0.999m, new SliResult(9_856, 10_000, 0));

        Assert.Equal(14.4m, burn);
    }

    [Fact]
    public void BurnRate_FiveMinuteFixture_IsSixTimes()
    {
        var burn = BurnRateCalculator.Calculate(0.999m, new SliResult(994, 1_000, 0));

        Assert.Equal(6m, burn);
    }

    [Fact]
    public void BurnRate_SixHourFixture_IsThreeTimes()
    {
        var burn = BurnRateCalculator.Calculate(0.999m, new SliResult(9_970, 10_000, 0));

        Assert.Equal(3m, burn);
    }

    [Fact]
    public void BurnRate_ThirtyMinuteFixture_IsThreeTimes()
    {
        var burn = BurnRateCalculator.Calculate(0.999m, new SliResult(9_970, 10_000, 0));

        Assert.Equal(3m, burn);
    }

    [Fact]
    public void BurnRate_OneDayFixture_IsThreeTimes()
    {
        var burn = BurnRateCalculator.Calculate(0.999m, new SliResult(997_000, 1_000_000, 0));

        Assert.Equal(3m, burn);
    }

    [Fact]
    public void BurnRate_ThreeDayFixture_IsOneTime()
    {
        var burn = BurnRateCalculator.Calculate(0.999m, new SliResult(99_900, 100_000, 0));

        Assert.Equal(1m, burn);
    }

    [Fact]
    public void StandardFastPageRule_DerivesTwoPercentBudgetSpendForThirtyDays()
    {
        var rule = MultiWindowAlertRule.StandardRules.Single(item => item.Name == "fast-page");

        Assert.Equal(2m, rule.DerivedBudgetSpendPercent(TimeSpan.FromDays(30)));
    }

    [Fact]
    public void MultiWindowAlert_WhenBothWindowsBreach_Fires()
    {
        var rule = MultiWindowAlertRule.StandardRules.Single(item => item.Name == "fast-page");
        var alert = MultiWindowAlertEvaluator.Evaluate(null, AlertInput(rule, 14.4m, 6m));

        Assert.Equal(AlertState.Firing, alert.State);
        Assert.Equal(Now, alert.DetectedAt);
        Assert.Equal(Now, alert.FiredAt);
    }

    [Fact]
    public void MultiWindowAlert_WhenOnlyLongWindowBreaches_DoesNotFire()
    {
        var rule = MultiWindowAlertRule.StandardRules.Single(item => item.Name == "fast-page");
        var alert = MultiWindowAlertEvaluator.Evaluate(null, AlertInput(rule, 15m, 5.9m));

        Assert.Equal(AlertState.Resolved, alert.State);
    }

    [Fact]
    public void MultiWindowAlert_OnRecovery_ResetsAndRecordsRecovery()
    {
        var rule = MultiWindowAlertRule.StandardRules.Single(item => item.Name == "fast-page");
        var firing = MultiWindowAlertEvaluator.Evaluate(null, AlertInput(rule, 15m, 7m));
        var recovered = MultiWindowAlertEvaluator.Evaluate(firing, AlertInput(rule, 1m, 1m, Now.AddMinutes(5)));

        Assert.Equal(AlertState.Resolved, recovered.State);
        Assert.Equal(Now.AddMinutes(5), recovered.RecoveredAt);
    }

    [Fact]
    public void MultiWindowAlert_DuringMaintenance_IsSuppressed()
    {
        var rule = MultiWindowAlertRule.StandardRules.Single(item => item.Name == "fast-page");
        var alert = MultiWindowAlertEvaluator.Evaluate(null, AlertInput(rule, 15m, 7m, maintenance: true));

        Assert.Equal(AlertState.Suppressed, alert.State);
        Assert.Contains("maintenance", alert.SuppressionReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MultiWindowAlert_DuringOpenIncident_IsSuppressed()
    {
        var rule = MultiWindowAlertRule.StandardRules.Single(item => item.Name == "fast-page");
        var alert = MultiWindowAlertEvaluator.Evaluate(null, AlertInput(rule, 15m, 7m, incident: true));

        Assert.Equal(AlertState.Suppressed, alert.State);
        Assert.Contains("incident", alert.SuppressionReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MultiWindowAlert_RecordsDetectionLagFromObservedData()
    {
        var rule = MultiWindowAlertRule.StandardRules.Single(item => item.Name == "fast-page");
        var alert = MultiWindowAlertEvaluator.Evaluate(null, AlertInput(rule, 15m, 7m, dataObservedAt: Now.AddMinutes(-2)));

        Assert.Equal(TimeSpan.FromMinutes(2), alert.DetectionLag);
    }

    [Fact]
    public void ProjectedExhaustion_WithFiftyPercentRemainingAndTwoTimesBurn_IsSevenPointFiveDays()
    {
        var projected = ProjectedExhaustionCalculator.Calculate(Now, 50m, 2m, TimeSpan.FromDays(30));

        Assert.Equal(Now.AddHours(180), projected);
    }

    [Fact]
    public void ProjectedExhaustion_WithZeroBurn_HasNoProjection()
    {
        Assert.Null(ProjectedExhaustionCalculator.Calculate(Now, 50m, 0m, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void ErrorBudgetPolicy_AtHealthyBudget_AllowsDeploy()
    {
        var decision = ErrorBudgetPolicyEngine.Evaluate("checkout", 50m);

        Assert.True(decision.Allowed);
        Assert.Equal(BudgetPolicyAction.Allow, decision.Action);
    }

    [Fact]
    public void ErrorBudgetPolicy_BelowFiftyPercent_WarnsButAllowsDeploy()
    {
        var decision = ErrorBudgetPolicyEngine.Evaluate("checkout", 49.9m);

        Assert.True(decision.Allowed);
        Assert.Equal(BudgetPolicyAction.Warn, decision.Action);
    }

    [Fact]
    public void ErrorBudgetPolicy_BelowTwentyFivePercent_DeniesRiskyDeploy()
    {
        var decision = ErrorBudgetPolicyEngine.Evaluate("checkout", 24.9m);

        Assert.False(decision.Allowed);
        Assert.Equal(BudgetPolicyAction.FreezeRiskyDeploys, decision.Action);
    }

    [Fact]
    public void ErrorBudgetPolicy_UsesConfiguredThresholds()
    {
        var decision = ErrorBudgetPolicyEngine.Evaluate("checkout", 55m, new ErrorBudgetPolicy(70m, 40m));

        Assert.True(decision.Allowed);
        Assert.Equal(BudgetPolicyAction.Warn, decision.Action);
    }

    [Fact]
    public void ErrorBudgetPolicy_WhenExhausted_DeniesAllChanges()
    {
        var decision = ErrorBudgetPolicyEngine.Evaluate("checkout", 0m);

        Assert.False(decision.Allowed);
        Assert.Equal(BudgetPolicyAction.FreezeAllChanges, decision.Action);
    }

    [Fact]
    public void IncidentMetrics_FromConstructedTimeline_ComputesMttdMttaAndMttr()
    {
        var incident = ResolvedIncident();

        var metrics = IncidentMetricsCalculator.Calculate(incident);

        Assert.Equal(TimeSpan.FromMinutes(2), metrics.MeanTimeToDetect);
        Assert.Equal(TimeSpan.FromMinutes(2), metrics.MeanTimeToAcknowledge);
        Assert.Equal(TimeSpan.FromMinutes(20), metrics.MeanTimeToResolve);
    }

    [Fact]
    public void IncidentBudgetAttribution_UsesOnlyIncidentWindow()
    {
        var incident = ResolvedIncident();
        var impact = IncidentBudgetAttributor.Calculate(
            incident,
            Slo(0.999m),
            Availability(),
            [Metric(Now.AddMinutes(-5), 1_000, 2), Metric(Now.AddHours(-2), 10_000, 500)],
            Now);

        Assert.Equal(2, impact.BadEventsOrMinutes);
        Assert.Equal(2m, impact.ErrorBudgetConsumed);
        Assert.Equal(200m, impact.ErrorBudgetImpactPercent);
    }

    [Fact]
    public void Postmortem_StateMachine_AllowsDraftReviewApprovalPublication()
    {
        var postmortem = Postmortem().SubmitForReview().Approve().Publish();

        Assert.Equal(PostmortemReviewState.Published, postmortem.ReviewState);
    }

    [Fact]
    public void Postmortem_StateMachine_RejectsSkippingReview()
    {
        Assert.Throws<DomainRuleViolationException>(() => Postmortem().Approve());
    }

    [Fact]
    public void Postmortem_OverdueActionItems_ExcludesCompletedActions()
    {
        var postmortem = Postmortem()
            .AddActionItem("Replace retry policy", "Asha", new DateOnly(2026, 1, 1))
            .AddActionItem("Add synthetic check", "Nia", new DateOnly(2026, 1, 1));
        postmortem = postmortem.CompleteActionItem(postmortem.ActionItems[1].Id, Now);

        var overdue = PostmortemThemeAnalyzer.OverdueActions([postmortem], Now);

        Assert.Single(overdue);
        Assert.Equal("Asha", overdue[0].Owner);
    }

    [Fact]
    public void PostmortemThemes_GroupsRecurringContributingFactors()
    {
        var first = Postmortem() with { ContributingFactors = ["Missing retry budget", "Runbook gap"] };
        var second = Postmortem() with { ContributingFactors = ["missing retry budget"] };

        var themes = PostmortemThemeAnalyzer.Analyze([first, second]);

        Assert.Equal("Missing retry budget", themes[0].Factor);
        Assert.Equal(2, themes[0].Count);
    }

    [Fact]
    public void AlertQuality_ComputesAlertToIncidentRatioAndFalsePositiveRate()
    {
        var report = AlertQualityAnalyzer.Analyze(
            [Alert(AlertState.Resolved), Alert(AlertState.Firing, Guid.NewGuid())],
            [ResolvedIncident()]);

        Assert.Equal(2m, report.AlertToIncidentRatio);
        Assert.Equal(50m, report.FalsePositiveRate);
    }

    [Fact]
    public void AlertQuality_DetectsFlappingFromStateTransitions()
    {
        var observations = new[]
        {
            new AlertObservation("checkout", "fast-page", AlertState.Firing, Now.AddMinutes(-4)),
            new AlertObservation("checkout", "fast-page", AlertState.Resolved, Now.AddMinutes(-3)),
            new AlertObservation("checkout", "fast-page", AlertState.Firing, Now.AddMinutes(-2)),
            new AlertObservation("checkout", "fast-page", AlertState.Resolved, Now.AddMinutes(-1))
        };

        var flapping = AlertQualityAnalyzer.DetectFlapping(observations);

        Assert.Single(flapping);
        Assert.Equal(3, flapping[0].StateTransitions);
    }

    [Fact]
    public void ServiceDependencyGraph_DetectsCycle()
    {
        var alpha = Service("alpha", ["beta"]);
        var beta = Service("beta", ["alpha"]);

        Assert.True(ServiceDependencyGraph.HasCycle([alpha, beta]));
        Assert.Throws<DomainRuleViolationException>(() => ServiceDependencyGraph.EnsureAcyclic([alpha, beta]));
    }

    [Fact]
    public void ServiceScorecard_HealthyMatureServiceScoresOneHundred()
    {
        var scorecard = ServiceScorecardCalculator.Calculate(Service("checkout"), true, true, 80m, DependencyRisk.Healthy);

        Assert.Equal(100, scorecard.Score);
        Assert.Empty(scorecard.Recommendations);
    }

    [Fact]
    public void MetricRetention_RollsUpExpiredRawMetricsAndExpiresOldMetrics()
    {
        var clock = new FakeClock(Now);
        var result = MetricRetentionEngine.Apply(
        [
            Metric(Now.AddHours(-49), 100, 1),
            Metric(Now.AddHours(-49).AddMinutes(10), 200, 2),
            Metric(Now.AddHours(-2), 100, 0),
            Metric(Now.AddDays(-100), 100, 0)
        ],
        new MetricRetentionPolicy(TimeSpan.FromHours(48), TimeSpan.FromDays(90)),
        clock.UtcNow);

        Assert.Single(result.RetainedRawSamples);
        Assert.Single(result.HourlyRollups);
        Assert.Equal(300, result.HourlyRollups[0].Requests);
        Assert.Equal(1, result.ExpiredSamples);
    }

    [Fact]
    public void Simulator_PartialOutageProducesElevatedErrorRate()
    {
        var samples = new SyntheticTelemetrySimulator().Generate(new TrafficSimulationRequest(
            "checkout",
            Now.AddHours(-1),
            Now,
            TimeSpan.FromMinutes(5),
            100,
            "/checkout",
            "eu-west",
            "Tier0",
            0.0001m,
            new IncidentInjection(SyntheticIncidentKind.PartialOutage, Now.AddHours(-1), Now, 1m)));

        Assert.NotEmpty(samples);
        Assert.All(samples, sample => Assert.True(sample.Errors >= sample.Requests * 0.79m));
    }

    private static SliDefinition Availability() =>
        SliDefinition.Create("checkout availability", "checkout", SliAggregationMode.RequestBased, SliKind.Availability, SliFilter.All, null, Now);

    private static SliDefinition WindowAvailability() =>
        SliDefinition.Create("checkout availability", "checkout", SliAggregationMode.WindowBased, SliKind.Availability, SliFilter.All, null, Now);

    private static SloDefinition Slo(
        decimal target,
        SloWindowKind kind = SloWindowKind.Rolling,
        int rollingDays = 30,
        CalendarWindowPeriod period = CalendarWindowPeriod.Monthly) =>
        SloDefinition.Create("checkout availability", Availability().Id, "checkout", target, kind, rollingDays, period, Now);

    private static MetricSample Metric(
        DateTimeOffset at,
        long requests,
        long errors,
        string endpoint = "/checkout",
        long? latencyGood = null,
        long? qualityGood = null,
        long? qualityValid = null,
        long? freshnessGood = null,
        long? freshnessValid = null,
        long? probeGood = null,
        long? probeTotal = null,
        IReadOnlyList<LatencyHistogramBucket>? histogram = null) =>
        MetricSample.Create(
            "checkout",
            at,
            endpoint,
            "eu-west",
            "Tier0",
            requests,
            errors,
            latencyGood ?? requests - errors,
            qualityGood ?? requests,
            qualityValid ?? requests,
            freshnessGood ?? requests,
            freshnessValid ?? requests,
            probeGood ?? (errors == 0 ? 1 : 0),
            probeTotal ?? 1,
            80,
            150,
            histogram);

    private static AlertEvaluationInput AlertInput(
        MultiWindowAlertRule rule,
        decimal longBurn,
        decimal shortBurn,
        DateTimeOffset? at = null,
        bool maintenance = false,
        bool incident = false,
        DateTimeOffset? dataObservedAt = null) =>
        new(Guid.NewGuid(), "checkout", rule, longBurn, shortBurn, at ?? Now, dataObservedAt ?? Now, maintenance, incident);

    private static AlertInstance Alert(AlertState state, Guid? incidentId = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "checkout", "fast-page", AlertSeverity.Page, state, 1m, 1m, Now, Now,
            state == AlertState.Resolved ? Now : null, null, null, null, incidentId, Now, TimeSpan.Zero, Now);

    private static Incident ResolvedIncident()
    {
        var incident = Incident.Declare("Checkout outage", IncidentSeverity.Sev1, ["checkout"], "Ada", "Nia", Now.AddMinutes(-22), Now.AddMinutes(-17), "Ada")
            .AddTimelineEvent(Now.AddMinutes(-20), "Alerting", IncidentTimelineEventType.Detected, "Fast burn detected")
            .Acknowledge(Now.AddMinutes(-18), "Ada", "Acknowledged")
            .Mitigate(Now.AddMinutes(-12), "Ada", "Rollback complete")
            .Resolve(Now, "Ada", "Traffic healthy");
        return incident;
    }

    private static Postmortem Postmortem() =>
        Northstar.Reliability.Domain.Postmortems.Postmortem.Create(
            Guid.NewGuid(),
            "Checkout outage postmortem",
            "A transient failure reached customers.",
            "2 failed checkout requests in the attributed window.",
            ["Timeline reviewed."],
            ["Runbook gap"],
            "Fast collaboration.",
            "Detection needed more context.",
            Now);

    private static ServiceDefinition Service(string slug, IReadOnlyList<string>? dependencies = null) =>
        ServiceDefinition.Create(
            slug,
            slug,
            CriticalityTier.Tier1,
            "Commerce",
            "commerce-primary",
            $"https://github.example.invalid/{slug}",
            $"https://runbooks.example.invalid/{slug}",
            dependencies,
            Now);
}
