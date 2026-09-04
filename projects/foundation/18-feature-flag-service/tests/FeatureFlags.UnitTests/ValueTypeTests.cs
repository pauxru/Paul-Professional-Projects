using System.Text.Json;
using FeatureFlags.Domain;
using FeatureFlags.Sdk;

namespace FeatureFlags.UnitTests;

public sealed class ValueTypeTests
{
    [Fact]
    public void Evaluate_StringFlag_ReturnsSelectedStringVariation()
    {
        var flag = new FlagDefinition
        {
            Key = "copy", Name = "copy", ValueType = FlagValueType.String, IsOn = true, OffVariation = 0, FallthroughVariation = 1,
            Variations = [FlagVariation.Create(0, "control", "old"), FlagVariation.Create(1, "treatment", "new")]
        };
        var result = new FlagEvaluator().Evaluate(TestFlags.Configuration(flag), "copy", TestFlags.Context(), DateTimeOffset.UnixEpoch);
        Assert.Equal("new", result.Value.GetString());
    }

    [Fact]
    public void Evaluate_NumberFlag_ReturnsDecimalCompatibleVariation()
    {
        var flag = new FlagDefinition
        {
            Key = "timeout", Name = "timeout", ValueType = FlagValueType.Number, IsOn = true, OffVariation = 0, FallthroughVariation = 1,
            Variations = [FlagVariation.Create(0, "safe", 10), FlagVariation.Create(1, "normal", 30.5m)]
        };
        var result = new FlagEvaluator().Evaluate(TestFlags.Configuration(flag), "timeout", TestFlags.Context(), DateTimeOffset.UnixEpoch);
        Assert.True(result.Value.TryGetDecimal(out var value));
        Assert.Equal(30.5m, value);
    }

    [Fact]
    public void Evaluate_JsonFlag_ReturnsObjectVariation()
    {
        var flag = new FlagDefinition
        {
            Key = "config", Name = "config", ValueType = FlagValueType.Json, IsOn = true, OffVariation = 0, FallthroughVariation = 1,
            Variations = [FlagVariation.Create(0, "safe", new { enabled = false }), FlagVariation.Create(1, "active", new { enabled = true, limit = 25 })]
        };
        var result = new FlagEvaluator().Evaluate(TestFlags.Configuration(flag), "config", TestFlags.Context(), DateTimeOffset.UnixEpoch);
        Assert.True(result.Value.GetProperty("enabled").GetBoolean());
        Assert.Equal(25, result.Value.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Sdk_TypedVariationMethods_ConvertStringNumberAndJsonWithoutNetwork()
    {
        var stringFlag = new FlagDefinition { Key = "copy", Name = "copy", ValueType = FlagValueType.String, IsOn = true, OffVariation = 0, FallthroughVariation = 1, Variations = [FlagVariation.Create(0, "off", "a"), FlagVariation.Create(1, "on", "b")] };
        var numberFlag = new FlagDefinition { Key = "number", Name = "number", ValueType = FlagValueType.Number, IsOn = true, OffVariation = 0, FallthroughVariation = 1, Variations = [FlagVariation.Create(0, "off", 1), FlagVariation.Create(1, "on", 2.5m)] };
        var jsonFlag = new FlagDefinition { Key = "json", Name = "json", ValueType = FlagValueType.Json, IsOn = true, OffVariation = 0, FallthroughVariation = 1, Variations = [FlagVariation.Create(0, "off", new { on = false }), FlagVariation.Create(1, "on", new { on = true })] };
        using var http = new HttpClient(new StubHandler(_ => throw new HttpRequestException("network should not be used")));
        await using var sdk = new FeatureFlagClient(http, TestFlags.ClientOptions());
        sdk.LoadConfiguration(TestFlags.Configuration(stringFlag, numberFlag, jsonFlag));
        Assert.Equal("b", sdk.StringVariation("copy", TestFlags.Context(), "fallback"));
        Assert.Equal(2.5m, sdk.NumberVariation("number", TestFlags.Context(), 0));
        Assert.True(sdk.JsonVariation("json", TestFlags.Context(), JsonSerializer.SerializeToElement(new { })).GetProperty("on").GetBoolean());
    }
}
