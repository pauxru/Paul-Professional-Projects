using AgentPlatform.Domain.Security;

namespace AgentPlatform.UnitTests.Security;

/// <summary>
/// Proves the <c>calculate</c> tool's expression evaluator does real arithmetic but cannot escape
/// into identifiers, reflection, code or unbounded input — model output is data, not code.
/// </summary>
public sealed class SafeExpressionEvaluatorTests
{
    private readonly SafeExpressionEvaluator _eval = new();

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("2 ^ 3 ^ 2", 512)]        // right associative
    [InlineData("-(3 + 4)", -7)]
    [InlineData("10 % 3", 1)]
    public void Evaluates_arithmetic(string expression, double expected)
        => Assert.Equal(expected, _eval.Evaluate(expression), 6);

    [Theory]
    [InlineData("abs(-5)", 5)]
    [InlineData("min(3, 1, 2)", 1)]
    [InlineData("max(3, 1, 2)", 3)]
    [InlineData("sqrt(9)", 3)]
    [InlineData("round(2.5)", 3)]
    [InlineData("floor(2.9)", 2)]
    [InlineData("ceil(2.1)", 3)]
    public void Evaluates_allow_listed_functions(string expression, double expected)
        => Assert.Equal(expected, _eval.Evaluate(expression), 6);

    [Theory]
    [InlineData("x + 1")]                       // bare identifier / variable
    [InlineData("System.Environment.Exit(0)")]  // reflection-ish
    [InlineData("__import__('os')")]            // not our grammar
    [InlineData("eval(1)")]                     // unknown function
    [InlineData("1 +")]                         // truncated
    [InlineData("(1 + 2")]                      // unbalanced
    public void Rejects_non_arithmetic_input(string expression)
    {
        Assert.False(_eval.TryEvaluate(expression, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Division_by_zero_is_rejected()
        => Assert.Throws<ExpressionException>(() => _eval.Evaluate("1 / 0"));

    [Fact]
    public void Overlong_expression_is_rejected()
    {
        var huge = string.Join(" + ", Enumerable.Repeat("1", 400));
        Assert.Throws<ExpressionException>(() => _eval.Evaluate(huge));
    }

    [Fact]
    public void Sqrt_of_negative_is_rejected()
        => Assert.Throws<ExpressionException>(() => _eval.Evaluate("sqrt(-1)"));
}
