using FeatureFlags.Domain;

namespace FeatureFlags.UnitTests;

public sealed class FlagEvaluatorTests
{
    private readonly FlagEvaluator _evaluator = new();
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-01-15T12:00:00Z");

    [Fact]
    public void Evaluate_KillSwitchOff_ShortCircuitsTargetsRulesAndRollout()
    {
        var flag = TestFlags.Boolean(on: false, fallthrough: 1) with
        {
            Targets = [new IndividualTarget(1, ["u1"])],
            Rules = [new TargetingRule { VariationIndex = 1, Clauses = [RuleClause.ForEquals("country", "KE")] }],
            Rollout = new PercentageRollout([new WeightedVariation(1, 100_000)])
        };
        var result = _evaluator.Evaluate(TestFlags.Configuration(flag), flag.Key, TestFlags.Context("u1", new { country = "KE" }), _now);
        Assert.False(result.Value.GetBoolean());
        Assert.Equal(0, result.VariationIndex);
        Assert.Equal(EvaluationReasonKind.Off, result.Reason.Kind);
    }

    [Fact]
    public void Evaluate_IndividualTarget_PrecedesSegmentAndRules()
    {
        var flag = TestFlags.Boolean(fallthrough: 0) with
        {
            Targets = [new IndividualTarget(1, ["u1"])],
            SegmentTargets = [new SegmentTarget(2, "operators")],
            Rules = [new TargetingRule { VariationIndex = 2, Clauses = [RuleClause.ForEquals("country", "KE")] }]
        };
        var config = TestFlags.Configuration(flag) with { Segments = [new SegmentDefinition { Key = "operators", IncludedContextKeys = ["u1"] }] };
        var result = _evaluator.Evaluate(config, flag.Key, TestFlags.Context("u1", new { country = "KE" }), _now);
        Assert.Equal(1, result.VariationIndex);
        Assert.Equal(EvaluationReasonKind.TargetMatch, result.Reason.Kind);
    }

    [Fact]
    public void Evaluate_SegmentMembership_UsesReusableRules()
    {
        var flag = TestFlags.Boolean() with { SegmentTargets = [new SegmentTarget(1, "kenya")], FallthroughVariation = 0 };
        var config = TestFlags.Configuration(flag) with
        {
            Segments = [new SegmentDefinition { Key = "kenya", Rules = [new TargetingRule { VariationIndex = 0, Clauses = [RuleClause.ForEquals("country", "KE")] }] }]
        };
        var result = _evaluator.Evaluate(config, flag.Key, TestFlags.Context(attributes: new { country = "KE" }), _now);
        Assert.Equal(1, result.VariationIndex);
        Assert.Equal(EvaluationReasonKind.SegmentMatch, result.Reason.Kind);
    }

    [Fact]
    public void Evaluate_NestedSegmentExclusion_WinsOverNestedInclusion()
    {
        var flag = TestFlags.Boolean() with { SegmentTargets = [new SegmentTarget(1, "outer")], FallthroughVariation = 0 };
        var config = TestFlags.Configuration(flag) with
        {
            Segments = [
                new SegmentDefinition { Key = "inner", IncludedContextKeys = ["u1"] },
                new SegmentDefinition { Key = "outer", IncludedSegments = ["inner"], ExcludedSegments = ["inner"] }
            ]
        };
        var result = _evaluator.Evaluate(config, flag.Key, TestFlags.Context("u1"), _now);
        Assert.Equal(0, result.VariationIndex);
        Assert.Equal(EvaluationReasonKind.Fallthrough, result.Reason.Kind);
    }

    [Fact]
    public void Evaluate_RulesAreEvaluatedInOrder_AndExposeRuleIndex()
    {
        var flag = TestFlags.Boolean() with
        {
            Rules = [
                new TargetingRule { VariationIndex = 1, Clauses = [RuleClause.ForEquals("country", "KE")] },
                new TargetingRule { VariationIndex = 2, Clauses = [RuleClause.ForEquals("country", "KE")] }
            ]
        };
        var result = _evaluator.Evaluate(TestFlags.Configuration(flag), flag.Key, TestFlags.Context(attributes: new { country = "KE" }), _now);
        Assert.Equal(1, result.VariationIndex);
        Assert.Equal(EvaluationReasonKind.RuleMatch, result.Reason.Kind);
        Assert.Equal(0, result.Reason.RuleIndex);
    }

    [Fact]
    public void Evaluate_PrerequisiteFailure_ReturnsOffVariationWithReason()
    {
        var prerequisite = TestFlags.Boolean("prerequisite", on: false, fallthrough: 1);
        var dependent = TestFlags.Boolean("dependent", fallthrough: 1) with { Prerequisites = [new FlagPrerequisite("prerequisite", 1)] };
        var result = _evaluator.Evaluate(TestFlags.Configuration(prerequisite, dependent), "dependent", TestFlags.Context(), _now);
        Assert.Equal(0, result.VariationIndex);
        Assert.Equal(EvaluationReasonKind.PrerequisiteFailed, result.Reason.Kind);
    }

    [Fact]
    public void Evaluate_SatisfiedPrerequisite_AllowsFallthrough()
    {
        var prerequisite = TestFlags.Boolean("prerequisite", fallthrough: 1);
        var dependent = TestFlags.Boolean("dependent", fallthrough: 1) with { Prerequisites = [new FlagPrerequisite("prerequisite", 1)] };
        var result = _evaluator.Evaluate(TestFlags.Configuration(prerequisite, dependent), "dependent", TestFlags.Context(), _now);
        Assert.True(result.Value.GetBoolean());
        Assert.Equal(EvaluationReasonKind.Fallthrough, result.Reason.Kind);
    }

    [Fact]
    public void Evaluate_PrerequisiteCycle_ReturnsTypedErrorAndValidatorFindsIt()
    {
        var first = TestFlags.Boolean("first") with { Prerequisites = [new FlagPrerequisite("second", 1)] };
        var second = TestFlags.Boolean("second") with { Prerequisites = [new FlagPrerequisite("first", 1)] };
        var config = TestFlags.Configuration(first, second);
        var result = _evaluator.Evaluate(config, "first", TestFlags.Context(), _now);
        Assert.Equal(EvaluationReasonKind.Error, result.Reason.Kind);
        Assert.Equal(EvaluationErrorKind.PrerequisiteCycle, result.Reason.ErrorKind);
        Assert.NotEmpty(PrerequisiteGraphValidator.FindCycle(config.Flags));
    }

    [Fact]
    public void Evaluate_ScheduledActivationAndTemporaryExpiry_UseProvidedClockValue()
    {
        var clock = new FakeClock(_now);
        var flag = TestFlags.Boolean(fallthrough: 1) with
        {
            ActivateAt = clock.UtcNow.AddHours(1),
            TemporaryUntil = clock.UtcNow.AddHours(2)
        };
        var config = TestFlags.Configuration(flag);
        Assert.Equal(EvaluationReasonKind.Off, _evaluator.Evaluate(config, flag.Key, TestFlags.Context(), clock.UtcNow).Reason.Kind);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(_evaluator.Evaluate(config, flag.Key, TestFlags.Context(), clock.UtcNow).Value.GetBoolean());
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(EvaluationReasonKind.Off, _evaluator.Evaluate(config, flag.Key, TestFlags.Context(), clock.UtcNow).Reason.Kind);
    }

    [Fact]
    public void Evaluate_ArchivedFlag_ReturnsOffVariation()
    {
        var flag = TestFlags.Boolean(fallthrough: 1) with { LifecycleStatus = FlagLifecycleStatus.Archived };
        var result = _evaluator.Evaluate(TestFlags.Configuration(flag), flag.Key, TestFlags.Context(), _now);
        Assert.Equal(EvaluationReasonKind.Off, result.Reason.Kind);
        Assert.False(result.Value.GetBoolean());
    }

    [Fact]
    public void Evaluate_MissingFlag_ReturnsFlagNotFoundError()
    {
        var result = _evaluator.Evaluate(TestFlags.Configuration(), "absent", TestFlags.Context(), _now);
        Assert.Equal(EvaluationReasonKind.Error, result.Reason.Kind);
        Assert.Equal(EvaluationErrorKind.FlagNotFound, result.Reason.ErrorKind);
    }

    [Fact]
    public void Evaluate_InvalidVariation_ReturnsFallbackContract()
    {
        var flag = TestFlags.Boolean(fallthrough: 99) with { FallbackValue = TestFlags.Json(true) };
        var result = _evaluator.Evaluate(TestFlags.Configuration(flag), flag.Key, TestFlags.Context(), _now);
        Assert.True(result.Value.GetBoolean());
        Assert.Equal(EvaluationErrorKind.InvalidConfiguration, result.Reason.ErrorKind);
    }

    [Fact]
    public void ConfigurationValidator_RejectsDuplicateKeysOversizedRolloutAndCycles()
    {
        var first = TestFlags.Boolean("same") with { Rollout = new PercentageRollout([new WeightedVariation(1, 100_001)]) };
        var second = TestFlags.Boolean("same");
        var third = TestFlags.Boolean("third") with { Prerequisites = [new FlagPrerequisite("fourth", 1)] };
        var fourth = TestFlags.Boolean("fourth") with { Prerequisites = [new FlagPrerequisite("third", 1)] };
        var errors = FlagConfigurationValidator.Validate(TestFlags.Configuration(first, second, third, fourth));
        Assert.True(errors.Count >= 3);
    }
}
