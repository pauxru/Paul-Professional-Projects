using AuditPlatform.Domain.Schemas;
using Xunit;

namespace AuditPlatform.UnitTests;

public class SchemaCompatibilityTests
{
    private const string Original =
        "{ \"fields\": [ {\"name\":\"userId\",\"kind\":\"string\",\"required\":true}, {\"name\":\"role\",\"kind\":\"string\",\"required\":false} ] }";

    [Fact]
    public void AddingOptionalField_IsCompatible()
    {
        var oldSchema = SchemaDefinition.Parse(Original);
        var newSchema = SchemaDefinition.Parse("{ \"fields\": [ {\"name\":\"userId\",\"kind\":\"string\",\"required\":true}, {\"name\":\"role\",\"kind\":\"string\",\"required\":false}, {\"name\":\"email\",\"kind\":\"string\",\"required\":false} ] }");
        var (ok, breakages) = SchemaDefinition.CheckBackwardsCompatibility(oldSchema, newSchema);
        Assert.True(ok, string.Join(';', breakages));
    }

    [Fact]
    public void RemovingField_IsIncompatible()
    {
        var oldSchema = SchemaDefinition.Parse(Original);
        var newSchema = SchemaDefinition.Parse("{ \"fields\": [ {\"name\":\"userId\",\"kind\":\"string\",\"required\":true} ] }");
        var (ok, breakages) = SchemaDefinition.CheckBackwardsCompatibility(oldSchema, newSchema);
        Assert.False(ok);
        Assert.Contains(breakages, b => b.Contains("removed"));
    }

    [Fact]
    public void RetypingField_IsIncompatible()
    {
        var oldSchema = SchemaDefinition.Parse(Original);
        var newSchema = SchemaDefinition.Parse("{ \"fields\": [ {\"name\":\"userId\",\"kind\":\"integer\",\"required\":true}, {\"name\":\"role\",\"kind\":\"string\",\"required\":false} ] }");
        var (ok, breakages) = SchemaDefinition.CheckBackwardsCompatibility(oldSchema, newSchema);
        Assert.False(ok);
        Assert.Contains(breakages, b => b.Contains("type"));
    }

    [Fact]
    public void MakingOptionalRequired_IsIncompatible()
    {
        var oldSchema = SchemaDefinition.Parse(Original);
        var newSchema = SchemaDefinition.Parse("{ \"fields\": [ {\"name\":\"userId\",\"kind\":\"string\",\"required\":true}, {\"name\":\"role\",\"kind\":\"string\",\"required\":true} ] }");
        var (ok, breakages) = SchemaDefinition.CheckBackwardsCompatibility(oldSchema, newSchema);
        Assert.False(ok);
    }

    [Fact]
    public void AddingRequiredField_IsIncompatible()
    {
        var oldSchema = SchemaDefinition.Parse(Original);
        var newSchema = SchemaDefinition.Parse("{ \"fields\": [ {\"name\":\"userId\",\"kind\":\"string\",\"required\":true}, {\"name\":\"role\",\"kind\":\"string\",\"required\":false}, {\"name\":\"email\",\"kind\":\"string\",\"required\":true} ] }");
        var (ok, breakages) = SchemaDefinition.CheckBackwardsCompatibility(oldSchema, newSchema);
        Assert.False(ok);
    }

    [Fact]
    public void Validate_ReportsMissingRequiredField()
    {
        var schema = SchemaDefinition.Parse(Original);
        var payload = System.Text.Json.JsonDocument.Parse("{\"role\":\"admin\"}").RootElement;
        var errors = schema.Validate(payload).ToList();
        Assert.Contains(errors, e => e.Contains("userId"));
    }

    [Fact]
    public void Validate_AcceptsValidPayload()
    {
        var schema = SchemaDefinition.Parse(Original);
        var payload = System.Text.Json.JsonDocument.Parse("{\"userId\":\"u-1\",\"role\":\"user\"}").RootElement;
        Assert.Empty(schema.Validate(payload).ToList());
    }
}
