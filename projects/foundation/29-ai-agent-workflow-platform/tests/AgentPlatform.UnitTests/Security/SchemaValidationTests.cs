using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPlatform.Domain.Json;

namespace AgentPlatform.UnitTests.Security;

/// <summary>
/// Proves the JSON-schema validator accepts valid argument objects, rejects malformed ones, and
/// coerces safe scalar strings — the gate every model-produced tool argument passes through.
/// </summary>
public sealed class SchemaValidationTests
{
    private static JsonSchema Schema() => JsonSchema.Parse("""
    {
      "type": "object",
      "properties": {
        "name": { "type": "string", "minLength": 1, "maxLength": 20 },
        "age": { "type": "integer", "minimum": 0, "maximum": 130 },
        "active": { "type": "boolean" },
        "tier": { "type": "string", "enum": ["bronze", "silver", "gold"] }
      },
      "required": ["name", "age"],
      "additionalProperties": false
    }
    """);

    [Fact]
    public void Valid_object_passes()
    {
        var value = JsonNode.Parse("""{ "name": "Ada", "age": 30, "active": true, "tier": "gold" }""");
        var result = Schema().Validate(value);
        Assert.True(result.IsValid, result.Summary);
    }

    [Fact]
    public void Missing_required_property_fails()
    {
        var value = JsonNode.Parse("""{ "name": "Ada" }""");
        var result = Schema().Validate(value);
        Assert.False(result.IsValid);
        Assert.Contains("age", result.Summary);
    }

    [Fact]
    public void Wrong_non_coercible_type_fails()
    {
        var value = JsonNode.Parse("""{ "name": "Ada", "age": "not-a-number" }""");
        var result = Schema().Validate(value);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Additional_property_is_rejected()
    {
        var value = JsonNode.Parse("""{ "name": "Ada", "age": 30, "evil": "payload" }""");
        var result = Schema().Validate(value);
        Assert.False(result.IsValid);
        Assert.Contains("evil", result.Summary);
    }

    [Fact]
    public void Value_out_of_range_fails()
    {
        var value = JsonNode.Parse("""{ "name": "Ada", "age": 999 }""");
        var result = Schema().Validate(value);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Enum_violation_fails()
    {
        var value = JsonNode.Parse("""{ "name": "Ada", "age": 30, "tier": "platinum" }""");
        var result = Schema().Validate(value);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Coerces_numeric_string_to_integer()
    {
        var value = JsonNode.Parse("""{ "name": "Ada", "age": "42" }""");
        var (coerced, result) = Schema().CoerceAndValidate(value);
        Assert.True(result.IsValid, result.Summary);
        Assert.Equal(42L, coerced!["age"]!.GetValue<long>());
    }

    [Fact]
    public void Coerces_boolean_string_to_boolean()
    {
        var value = JsonNode.Parse("""{ "name": "Ada", "age": 30, "active": "true" }""");
        var (coerced, result) = Schema().CoerceAndValidate(value);
        Assert.True(result.IsValid, result.Summary);
        Assert.True(coerced!["active"]!.GetValue<bool>());
    }

    [Fact]
    public void Coercion_does_not_mutate_caller_input()
    {
        var value = (JsonObject)JsonNode.Parse("""{ "name": "Ada", "age": "42" }""")!;
        Schema().CoerceAndValidate(value);
        Assert.Equal(JsonValueKind.String, value["age"]!.GetValueKind());
    }
}
