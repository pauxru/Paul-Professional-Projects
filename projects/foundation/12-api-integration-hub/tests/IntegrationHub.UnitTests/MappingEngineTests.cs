using System.Text.Json.Nodes;
using IntegrationHub.Application;

namespace IntegrationHub.UnitTests;

public sealed class MappingEngineTests
{
    private readonly JsonNode _input = JsonNode.Parse("""
        {
          "name":"  Ada Lovelace  ",
          "first":"Ada",
          "last":"Lovelace",
          "amount":"12.50",
          "currency":"KSH",
          "date":"2026-09-03T10:15:00+03:00",
          "nested":{"value":"Hello"},
          "items":[{"sku":"A-1"},{"sku":"B-2"}],
          "empty":"",
          "active":true
        }
        """)!;

    [Theory]
    [InlineData("trim($.name)", "Ada Lovelace")]
    [InlineData("upper($.first)", "ADA")]
    [InlineData("lower($.last)", "lovelace")]
    public void Evaluate_StringFunctions_ReturnExpectedValue(string expression, string expected)
    {
        Assert.Equal(expected, Evaluate(expression)!.GetValue<string>());
    }

    [Fact]
    public void Evaluate_DateParse_ReturnsUtcIsoValue()
    {
        var value = Evaluate("date_parse('2026-09-03', 'yyyy-MM-dd')")!.GetValue<string>();
        Assert.StartsWith("2026-09-03T00:00:00", value);
    }

    [Fact]
    public void Evaluate_DateFormat_AppliesTimezoneAndFormat()
    {
        Assert.Equal("2026-09-03 07:15", Evaluate("date_format($.date, 'yyyy-MM-dd HH:mm', 'UTC')")!.GetValue<string>());
    }

    [Fact]
    public void Evaluate_DecimalScale_ReturnsScaledDecimal()
    {
        Assert.Equal(1250m, Evaluate("decimal_scale($.amount, 100)")!.GetValue<decimal>());
    }

    [Theory]
    [InlineData("KSH", "KES")]
    [InlineData("US$", "USD")]
    [InlineData("EUR", "EUR")]
    public void Evaluate_CurrencyMapping_NormalizesKnownCodes(string source, string expected)
    {
        Assert.Equal(expected, Evaluate($"currency('{source}')")!.GetValue<string>());
    }

    [Fact]
    public void Evaluate_Concat_JoinsValues()
    {
        Assert.Equal("Ada Lovelace", Evaluate("concat($.first, ' ', $.last)")!.GetValue<string>());
    }

    [Fact]
    public void Evaluate_Split_ReturnsRequestedSegment()
    {
        Assert.Equal("Lovelace", Evaluate("split($.name, ' ', 3)")!.GetValue<string>());
    }

    [Fact]
    public void Evaluate_LookupTable_ReturnsMappedValue()
    {
        var tables = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["countries"] = new Dictionary<string, string> { ["KE"] = "Kenya" }
        };
        var evaluator = new SafeExpressionEvaluator(tables);
        Assert.Equal("Kenya", evaluator.Evaluate("lookup('countries', 'KE')", _input)!.GetValue<string>());
    }

    [Fact]
    public void Evaluate_Default_ReplacesMissingValue()
    {
        Assert.Equal("fallback", Evaluate("default($.missing, 'fallback')")!.GetValue<string>());
    }

    [Fact]
    public void Evaluate_Coalesce_ReturnsFirstNonEmptyValue()
    {
        Assert.Equal("Ada", Evaluate("coalesce($.missing, $.empty, $.first)")!.GetValue<string>());
    }

    [Fact]
    public void Evaluate_Conditional_SelectsBranch()
    {
        Assert.Equal("yes", Evaluate("conditional(eq($.active, true), 'yes', 'no')")!.GetValue<string>());
    }

    [Fact]
    public void Evaluate_NestedPath_ReturnsNestedValue()
    {
        Assert.Equal("Hello", Evaluate("$.nested.value")!.GetValue<string>());
    }

    [Fact]
    public void Evaluate_ArrayIndex_ReturnsArrayItem()
    {
        Assert.Equal("B-2", Evaluate("$.items[1].sku")!.GetValue<string>());
    }

    [Fact]
    public void Mapping_ArrayTarget_CreatesNestedArray()
    {
        var result = new MappingEngine(new SafeExpressionEvaluator()).Map(
            _input,
            [new FieldMapping("$.lines[0].sku", "$.items[0].sku")]);
        Assert.Equal("A-1", result.Output["lines"]![0]!["sku"]!.GetValue<string>());
    }

    [Fact]
    public void Mapping_MissingField_WritesNullAndTrace()
    {
        var result = new MappingEngine(new SafeExpressionEvaluator()).Map(
            _input,
            [new FieldMapping("$.missing", "$.doesNotExist")]);
        Assert.Null(result.Output["missing"]);
        Assert.True(result.Trace.Single().Success);
    }

    [Fact]
    public void Mapping_TypeCoercionFailure_IsReportedPerField()
    {
        var result = new MappingEngine(new SafeExpressionEvaluator()).Map(
            _input,
            [new FieldMapping("$.scaled", "decimal_scale($.first, 100)")]);
        Assert.False(result.Trace.Single().Success);
        Assert.Contains("numeric", result.Trace.Single().Error);
    }

    [Theory]
    [InlineData("System.IO.File.ReadAllText('secret.txt')")]
    [InlineData("gettype($.name)")]
    [InlineData("new('System.Diagnostics.Process')")]
    public void Evaluate_UnsafeOrUnknownFunction_CannotEscapeWhitelist(string expression)
    {
        Assert.Throws<SafeExpressionException>(() => Evaluate(expression));
    }

    [Fact]
    public void Evaluate_ExcessiveNesting_IsRejected()
    {
        var expression = "$.first";
        for (var i = 0; i < 20; i++)
        {
            expression = $"trim({expression})";
        }
        Assert.Throws<SafeExpressionException>(() => Evaluate(expression));
    }

    [Fact]
    public void Mapping_FieldTrace_ContainsExpressionAndResult()
    {
        var result = new MappingEngine(new SafeExpressionEvaluator()).Map(
            _input,
            [new FieldMapping("$.customer.name", "trim($.name)")]);
        var trace = Assert.Single(result.Trace);
        Assert.Equal("trim($.name)", trace.Expression);
        Assert.Equal("\"Ada Lovelace\"", trace.ResultValue);
    }

    private JsonNode? Evaluate(string expression) => new SafeExpressionEvaluator().Evaluate(expression, _input);
}
