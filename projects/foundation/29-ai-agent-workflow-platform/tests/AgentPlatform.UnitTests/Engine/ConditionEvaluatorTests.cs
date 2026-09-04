using System.Text.Json.Nodes;
using AgentPlatform.Application.Engine;
using AgentPlatform.Domain.Workflows;

namespace AgentPlatform.UnitTests.Engine;

/// <summary>Proves deterministic condition branching — routing is data comparison, never a model call.</summary>
public sealed class ConditionEvaluatorTests
{
    [Fact]
    public void Equals_matches_scalar()
        => Assert.True(ConditionEvaluator.Evaluate(JsonValue.Create(true), ConditionOperator.Equals, JsonValue.Create(true)));

    [Fact]
    public void NotEquals_detects_difference()
        => Assert.True(ConditionEvaluator.Evaluate(JsonValue.Create("a"), ConditionOperator.NotEquals, JsonValue.Create("b")));

    [Fact]
    public void Contains_substring_is_case_insensitive()
        => Assert.True(ConditionEvaluator.Evaluate(JsonValue.Create("please AUTO_RESOLVE now"), ConditionOperator.Contains, JsonValue.Create("auto_resolve")));

    [Fact]
    public void Contains_array_membership()
    {
        var array = new JsonArray("get_ticket", "search");
        Assert.True(ConditionEvaluator.Evaluate(array, ConditionOperator.Contains, JsonValue.Create("search")));
    }

    [Theory]
    [InlineData(5, ConditionOperator.GreaterThan, 3, true)]
    [InlineData(3, ConditionOperator.GreaterThan, 5, false)]
    [InlineData(5, ConditionOperator.GreaterThanOrEqual, 5, true)]
    [InlineData(2, ConditionOperator.LessThan, 3, true)]
    public void Numeric_comparisons(int actual, ConditionOperator op, int expected, bool result)
        => Assert.Equal(result, ConditionEvaluator.Evaluate(JsonValue.Create(actual), op, JsonValue.Create(expected)));

    [Fact]
    public void Exists_is_true_for_present_value_and_false_for_null()
    {
        Assert.True(ConditionEvaluator.Evaluate(JsonValue.Create(0), ConditionOperator.Exists, null));
        Assert.False(ConditionEvaluator.Evaluate(null, ConditionOperator.Exists, null));
    }
}
