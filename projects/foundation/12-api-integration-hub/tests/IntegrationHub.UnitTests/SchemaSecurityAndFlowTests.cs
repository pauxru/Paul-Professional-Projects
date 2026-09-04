using System.Text.Json.Nodes;
using IntegrationHub.Application;
using IntegrationHub.Domain;

namespace IntegrationHub.UnitTests;

public sealed class SchemaSecurityAndFlowTests
{
    [Fact]
    public void ContractValidation_MissingRequiredField_ReturnsViolation()
    {
        var contract = Contract();
        var violations = new PayloadContractValidator().Validate(JsonNode.Parse("""{"id":"1"}"""), contract);
        Assert.Contains(violations, x => x.Path == "$.name");
    }

    [Fact]
    public void ContractDrift_RetypedField_IsDetected()
    {
        var report = new SchemaDriftDetector().Detect(JsonNode.Parse("""{"id":"1","name":42}"""), Contract());
        Assert.Contains(report.Changes, x => x.Path == "$.name" && x.Change == "retyped");
    }

    [Fact]
    public void ContractDrift_NewField_IsDetectedWhenAdditionalFieldsDisallowed()
    {
        var report = new SchemaDriftDetector().Detect(
            JsonNode.Parse("""{"id":"1","name":"Ada","unexpected":true}"""),
            Contract());
        Assert.Contains(report.Changes, x => x.Path == "$.unexpected" && x.Change == "new");
    }

    [Fact]
    public void ContractDrift_CompatiblePayload_HasNoDrift()
    {
        var report = new SchemaDriftDetector().Detect(JsonNode.Parse("""{"id":"1","name":"Ada"}"""), Contract());
        Assert.False(report.HasDrift);
    }

    [Fact]
    public async Task UrlGuard_PrivateAddress_IsRejected()
    {
        var guard = new ConnectorUrlGuard(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1" });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.ValidateAsync(new Uri("https://127.0.0.1/internal")));
    }

    [Fact]
    public async Task UrlGuard_HostOutsideAllowList_IsRejectedBeforeResolution()
    {
        var guard = new ConnectorUrlGuard(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api.example.test" });
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.ValidateAsync(new Uri("https://attacker.invalid")));
        Assert.Contains("allow-list", exception.Message);
    }

    [Fact]
    public void FlowVersioning_ActivateAndRollback_RestoresPreviousVersion()
    {
        var flow = new IntegrationFlow(Guid.NewGuid(), "CRM sync");
        flow.AddVersion("json", "{}", DateTimeOffset.Parse("2026-01-01"), "tester");
        flow.AddVersion("json", """{"v":2}""", DateTimeOffset.Parse("2026-01-02"), "tester");
        flow.Activate(2);
        flow.Rollback();
        Assert.Equal(1, flow.ActiveVersion);
    }

    [Fact]
    public void FlowVersioning_UnknownVersion_IsRejected()
    {
        var flow = new IntegrationFlow(Guid.NewGuid(), "CRM sync");
        flow.AddVersion("json", "{}", DateTimeOffset.Parse("2026-01-01"), "tester");
        Assert.Throws<DomainValidationException>(() => flow.Activate(99));
    }

    [Fact]
    public void FlowParser_Json_RoundTripsDefinition()
    {
        var parser = new FlowDefinitionParser();
        var original = Definition();
        var parsed = parser.Parse("json", parser.Serialize("json", original));
        Assert.Equal(original.Name, parsed.Name);
        Assert.Equal(2, parsed.Steps.Count);
    }

    [Fact]
    public void FlowParser_Yaml_RoundTripsDefinition()
    {
        var parser = new FlowDefinitionParser();
        var original = Definition();
        var parsed = parser.Parse("yaml", parser.Serialize("yaml", original));
        Assert.Equal(FlowTriggerKind.Schedule, parsed.Trigger.Kind);
        Assert.Equal("*/5 * * * *", parsed.Trigger.Cron);
        Assert.Equal("crm", parsed.Steps[1].Settings["connectorId"]);
    }

    private static JsonContract Contract() => new("contact", [
        new ContractField("$.id", ContractValueType.String),
        new ContractField("$.name", ContractValueType.String)
    ], false);

    private static FlowDefinition Definition() => new(
        "Scheduled CRM sync",
        new TriggerDefinition(FlowTriggerKind.Schedule, "*/5 * * * *"),
        [
            new FlowStepDefinition("trigger", FlowStepKind.Trigger, new Dictionary<string, string>()),
            new FlowStepDefinition("fetch", FlowStepKind.Fetch, new Dictionary<string, string> { ["connectorId"] = "crm", ["operation"] = "list" })
        ]);
}
