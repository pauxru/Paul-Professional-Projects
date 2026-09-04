using FeatureFlags.Domain;

namespace FeatureFlags.UnitTests;

public sealed class ClauseEvaluatorTests
{
    public static IEnumerable<object[]> PositiveOperators()
    {
        foreach (var value in Enum.GetValues<ClauseOperator>()) yield return [value];
    }

    [Theory]
    [MemberData(nameof(PositiveOperators))]
    public void ClauseEvaluator_EachOperatorWithMatchingTypedAttribute_ReturnsTrue(ClauseOperator @operator)
    {
        var (context, clause) = MatchingCase(@operator);
        Assert.True(ClauseEvaluator.IsMatch(context, clause));
    }

    [Theory]
    [MemberData(nameof(PositiveOperators))]
    public void ClauseEvaluator_EachOperatorWithNonMatchingAttribute_ReturnsFalse(ClauseOperator @operator)
    {
        var (context, clause) = NonMatchingCase(@operator);
        Assert.False(ClauseEvaluator.IsMatch(context, clause));
    }

    [Fact]
    public void ClauseEvaluator_NullAndMissingAttributes_HaveExplicitEqualitySemantics()
    {
        var context = TestFlags.Context();
        Assert.True(ClauseEvaluator.IsMatch(context, RuleClause.ForEquals("missing", null)));
        Assert.False(ClauseEvaluator.IsMatch(context, RuleClause.For("missing", ClauseOperator.NotEquals, null)));
        Assert.False(ClauseEvaluator.IsMatch(context, RuleClause.For("missing", ClauseOperator.Contains, "x")));
    }

    [Fact]
    public void ClauseEvaluator_NumericTypeMismatch_ReturnsFalseWithoutThrowing()
    {
        var context = TestFlags.Context(attributes: new { age = "not-a-number" });
        Assert.False(ClauseEvaluator.IsMatch(context, RuleClause.For("age", ClauseOperator.GreaterThan, 18)));
    }

    [Fact]
    public void ClauseEvaluator_BoundedRegexRejectsCatastrophicPatternWithoutThrowing()
    {
        var context = TestFlags.Context(attributes: new { text = new string('a', 4_096) + "!" });
        var clause = RuleClause.For("text", ClauseOperator.MatchesRegex, "(a+)+$");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = ClauseEvaluator.IsMatch(context, clause);
        stopwatch.Stop();
        Assert.False(result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    private static (EvaluationContext Context, RuleClause Clause) MatchingCase(ClauseOperator @operator) => @operator switch
    {
        ClauseOperator.Equals => (TestFlags.Context(attributes: new { country = "KE" }), RuleClause.ForEquals("country", "KE")),
        ClauseOperator.NotEquals => (TestFlags.Context(attributes: new { country = "KE" }), RuleClause.For("country", @operator, "UG")),
        ClauseOperator.In => (TestFlags.Context(attributes: new { country = "KE" }), RuleClause.ForMany("country", @operator, "UG", "KE")),
        ClauseOperator.NotIn => (TestFlags.Context(attributes: new { country = "KE" }), RuleClause.ForMany("country", @operator, "UG", "TZ")),
        ClauseOperator.Contains => (TestFlags.Context(attributes: new { roles = new[] { "reader", "admin" } }), RuleClause.For("roles", @operator, "admin")),
        ClauseOperator.StartsWith => (TestFlags.Context(attributes: new { email = "ops@example.test" }), RuleClause.For("email", @operator, "ops@")),
        ClauseOperator.EndsWith => (TestFlags.Context(attributes: new { email = "ops@example.test" }), RuleClause.For("email", @operator, ".test")),
        ClauseOperator.MatchesRegex => (TestFlags.Context(attributes: new { email = "ops@example.test" }), RuleClause.For("email", @operator, "^[a-z]+@example\\.test$")),
        ClauseOperator.GreaterThan => (TestFlags.Context(attributes: new { age = "21" }), RuleClause.For("age", @operator, 18)),
        ClauseOperator.LessThan => (TestFlags.Context(attributes: new { age = 17 }), RuleClause.For("age", @operator, "18")),
        ClauseOperator.Before => (TestFlags.Context(attributes: new { date = "2026-01-01T00:00:00Z" }), RuleClause.For("date", @operator, "2026-02-01T00:00:00Z")),
        ClauseOperator.After => (TestFlags.Context(attributes: new { date = "2026-03-01T00:00:00Z" }), RuleClause.For("date", @operator, "2026-02-01T00:00:00Z")),
        ClauseOperator.SemVerEqual => (TestFlags.Context(attributes: new { version = "v1.2.3" }), RuleClause.For("version", @operator, "1.2.3")),
        ClauseOperator.SemVerGreater => (TestFlags.Context(attributes: new { version = "2.0.0" }), RuleClause.For("version", @operator, "1.9.9")),
        _ => throw new ArgumentOutOfRangeException(nameof(@operator))
    };

    private static (EvaluationContext Context, RuleClause Clause) NonMatchingCase(ClauseOperator @operator) => @operator switch
    {
        ClauseOperator.Equals => (TestFlags.Context(attributes: new { country = "KE" }), RuleClause.ForEquals("country", "UG")),
        ClauseOperator.NotEquals => (TestFlags.Context(attributes: new { country = "KE" }), RuleClause.For("country", @operator, "KE")),
        ClauseOperator.In => (TestFlags.Context(attributes: new { country = "KE" }), RuleClause.ForMany("country", @operator, "UG", "TZ")),
        ClauseOperator.NotIn => (TestFlags.Context(attributes: new { country = "KE" }), RuleClause.ForMany("country", @operator, "KE", "TZ")),
        ClauseOperator.Contains => (TestFlags.Context(attributes: new { roles = new[] { "reader" } }), RuleClause.For("roles", @operator, "admin")),
        ClauseOperator.StartsWith => (TestFlags.Context(attributes: new { email = "ops@example.test" }), RuleClause.For("email", @operator, "admin@")),
        ClauseOperator.EndsWith => (TestFlags.Context(attributes: new { email = "ops@example.test" }), RuleClause.For("email", @operator, ".com")),
        ClauseOperator.MatchesRegex => (TestFlags.Context(attributes: new { email = "ops@example.test" }), RuleClause.For("email", @operator, "^admin@")),
        ClauseOperator.GreaterThan => (TestFlags.Context(attributes: new { age = 18 }), RuleClause.For("age", @operator, 18)),
        ClauseOperator.LessThan => (TestFlags.Context(attributes: new { age = 18 }), RuleClause.For("age", @operator, 18)),
        ClauseOperator.Before => (TestFlags.Context(attributes: new { date = "2026-02-01T00:00:00Z" }), RuleClause.For("date", @operator, "2026-02-01T00:00:00Z")),
        ClauseOperator.After => (TestFlags.Context(attributes: new { date = "2026-02-01T00:00:00Z" }), RuleClause.For("date", @operator, "2026-02-01T00:00:00Z")),
        ClauseOperator.SemVerEqual => (TestFlags.Context(attributes: new { version = "1.2.4" }), RuleClause.For("version", @operator, "1.2.3")),
        ClauseOperator.SemVerGreater => (TestFlags.Context(attributes: new { version = "1.2.3-beta" }), RuleClause.For("version", @operator, "1.2.3")),
        _ => throw new ArgumentOutOfRangeException(nameof(@operator))
    };
}
