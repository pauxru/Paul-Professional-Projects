using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using IntegrationHub.Application;
using IntegrationHub.Domain;
using IntegrationHub.Infrastructure;
using Microsoft.Extensions.Options;

namespace IntegrationHub.UnitTests;

public sealed class WebhookSecretAndHistoryTests
{
    [Fact]
    public async Task Webhook_ValidSignature_IsAccepted()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-03T10:00:00Z"));
        var nonceStore = new MemoryNonceStore();
        var body = Encoding.UTF8.GetBytes("""{"eventType":"contact.changed","data":{}}""");
        var timestamp = clock.UtcNow.ToUnixTimeSeconds().ToString();
        const string nonce = "nonce-1";
        const string secret = "test-secret";
        var signature = Sign(body, timestamp, nonce, secret);
        var result = await new WebhookVerifier(clock, nonceStore).VerifyAsync(
            body, signature, timestamp, nonce, secret, TimeSpan.FromMinutes(5));
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Webhook_InvalidSignature_IsRejected()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-03T10:00:00Z"));
        var result = await new WebhookVerifier(clock, new MemoryNonceStore()).VerifyAsync(
            Encoding.UTF8.GetBytes("{}"),
            new string('0', 64),
            clock.UtcNow.ToUnixTimeSeconds().ToString(),
            "nonce-2",
            "test-secret",
            TimeSpan.FromMinutes(5));
        Assert.False(result.IsValid);
        Assert.Equal("Signature mismatch.", result.Error);
    }

    [Fact]
    public async Task Webhook_ReplayedNonce_IsRejected()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-03T10:00:00Z"));
        var nonces = new MemoryNonceStore();
        var body = Encoding.UTF8.GetBytes("{}");
        var timestamp = clock.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = Sign(body, timestamp, "same", "secret");
        var verifier = new WebhookVerifier(clock, nonces);
        Assert.True((await verifier.VerifyAsync(body, signature, timestamp, "same", "secret", TimeSpan.FromMinutes(5))).IsValid);
        Assert.False((await verifier.VerifyAsync(body, signature, timestamp, "same", "secret", TimeSpan.FromMinutes(5))).IsValid);
    }

    [Fact]
    public async Task SecretStore_ReferenceResolutionAndRotation_ReturnsLatestValue()
    {
        var path = ArtifactPath("secret-rotation.enc");
        DeleteIfExists(path);
        var redactor = new SecretRedactor();
        var store = new EncryptedFileSecretStore(
            Options.Create(new SecretsOptions { FilePath = path, MasterKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=" }),
            new FakeClock(DateTimeOffset.Parse("2026-09-03T10:00:00Z")),
            redactor);
        await store.SetAsync("crm/apiKey", "first-value");
        Assert.Equal("first-value", await new SecretReferenceResolver(store).ResolveAsync("@secret:crm/apiKey", default));
        Assert.Equal(2, await store.RotateAsync("crm/apiKey", "second-value"));
        Assert.Equal("second-value", await store.GetAsync("crm/apiKey"));
        DeleteIfExists(path);
    }

    [Fact]
    public async Task SecretStore_FileDoesNotContainPlaintextSecret()
    {
        var path = ArtifactPath("secret-at-rest.enc");
        DeleteIfExists(path);
        var store = new EncryptedFileSecretStore(
            Options.Create(new SecretsOptions { FilePath = path, MasterKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=" }),
            new FakeClock(DateTimeOffset.UtcNow),
            new SecretRedactor());
        await store.SetAsync("payments/clientSecret", "never-store-me-in-plaintext");
        Assert.DoesNotContain("never-store-me-in-plaintext", await File.ReadAllTextAsync(path));
        DeleteIfExists(path);
    }

    [Fact]
    public async Task ExecutionHistory_RedactsSecretAndPiiFromStoredSnapshots()
    {
        const string secret = "super-sensitive-value";
        var redactor = new SecretRedactor();
        redactor.Register(secret);
        var execution = new MemoryExecutionStore();
        var flowId = Guid.NewGuid();
        var flow = TestFlowStore.WithActiveFlow(flowId, new FlowDefinition(
            "redaction",
            new TriggerDefinition(FlowTriggerKind.Manual),
            [new FlowStepDefinition("trigger", FlowStepKind.Trigger, new Dictionary<string, string>())]));
        var runner = new FlowRunner(
            flow,
            new FlowDefinitionParser(),
            new EmptyConnectorRegistry(),
            execution,
            new MemoryDeadLetterStore(),
            new MemoryCheckpointStore(),
            new MemoryIdempotencyStore(),
            new MemoryDriftStore(),
            redactor,
            new SafeExpressionEvaluator(),
            new PayloadContractValidator(),
            new SchemaDriftDetector(),
            new FakeClock(DateTimeOffset.Parse("2026-09-03T10:00:00Z")),
            new SequentialIdGenerator());
        await runner.RunAsync(
            flowId,
            JsonNode.Parse($$"""{"note":"{{secret}}","email":"person@example.test"}"""),
            "correlation",
            default);
        var snapshot = Assert.Single(execution.Steps).InputSnapshot!;
        Assert.DoesNotContain(secret, snapshot);
        Assert.DoesNotContain("person@example.test", snapshot);
        Assert.Contains("[REDACTED]", snapshot);
    }

    [Fact]
    public async Task DlqReplay_RepeatedRequest_DoesNotWriteTargetTwice()
    {
        var connector = new CountingConnector();
        var registry = new ConnectorRegistry([connector]);
        var execution = new MemoryExecutionStore();
        var originalRun = new RunSnapshot(Guid.NewGuid(), Guid.NewGuid(), 1, RunStatus.PartiallySucceeded, "original", DateTimeOffset.UtcNow);
        await execution.CreateRunAsync(originalRun, default);
        var deadLetters = new MemoryDeadLetterStore();
        var item = new DeadLetterItem(
            Guid.NewGuid(), originalRun.Id, "load", "batch", "stable-key",
            """{"connectorId":"target","operation":"write","payload":{"id":"1"}}""",
            "failed", DeadLetterStatus.Pending, DateTimeOffset.UtcNow);
        await deadLetters.AddAsync(item, default);
        var runner = new FlowRunner(
            new TestFlowStore(),
            new FlowDefinitionParser(),
            registry,
            execution,
            deadLetters,
            new MemoryCheckpointStore(),
            new MemoryIdempotencyStore(),
            new MemoryDriftStore(),
            new SecretRedactor(),
            new SafeExpressionEvaluator(),
            new PayloadContractValidator(),
            new SchemaDriftDetector(),
            new FakeClock(DateTimeOffset.UtcNow),
            new SequentialIdGenerator());
        await runner.ReplayAsync(item.Id, null, null, "replay-1", default);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            runner.ReplayAsync(item.Id, null, null, "replay-2", default));
        Assert.Equal(1, connector.Calls);
    }

    [Fact]
    public async Task FlowLoad_DuplicateBusinessRecord_WritesTargetOnce()
    {
        var connector = new CountingConnector();
        var flowId = Guid.NewGuid();
        var flow = TestFlowStore.WithActiveFlow(flowId, new FlowDefinition(
            "idempotent load",
            new TriggerDefinition(FlowTriggerKind.Manual),
            [
                new FlowStepDefinition("load", FlowStepKind.Load, new Dictionary<string, string>
                {
                    ["connectorId"] = "target",
                    ["operation"] = "write",
                    ["batchKey"] = "customers"
                })
            ]));
        var runner = new FlowRunner(
            flow,
            new FlowDefinitionParser(),
            new ConnectorRegistry([connector]),
            new MemoryExecutionStore(),
            new MemoryDeadLetterStore(),
            new MemoryCheckpointStore(),
            new MemoryIdempotencyStore(),
            new MemoryDriftStore(),
            new SecretRedactor(),
            new SafeExpressionEvaluator(),
            new PayloadContractValidator(),
            new SchemaDriftDetector(),
            new FakeClock(DateTimeOffset.UtcNow),
            new SequentialIdGenerator());
        var run = await runner.RunAsync(
            flowId,
            JsonNode.Parse("""[{"id":"same"},{"id":"same"}]"""),
            "correlation",
            default);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(1, connector.Calls);
    }

    [Fact]
    public async Task WebhookSource_InvalidPayload_IsRejectedAfterValidSignature()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-03T10:00:00Z"));
        const string secret = "webhook-secret";
        var secretStore = new MemorySecretStore(new Dictionary<string, string> { ["hook"] = secret });
        var contract = new JsonContract("event", [
            new ContractField("$.eventType", ContractValueType.String),
            new ContractField("$.data", ContractValueType.Object)
        ]);
        var connector = new WebhookSourceConnector(
            new WebhookVerifier(clock, new MemoryNonceStore()),
            secretStore,
            new PayloadContractValidator(),
            contract,
            "@secret:hook");
        var body = """{"eventType":"changed"}""";
        var timestamp = clock.UtcNow.ToUnixTimeSeconds().ToString();
        var envelope = new JsonObject
        {
            ["body"] = body,
            ["timestamp"] = timestamp,
            ["nonce"] = "payload-validation",
            ["signature"] = Sign(Encoding.UTF8.GetBytes(body), timestamp, "payload-validation", secret)
        };
        var error = await Assert.ThrowsAsync<ConnectorException>(() =>
            connector.ExecuteAsync("verify", envelope, new ConnectorExecutionContext("test")));
        Assert.Equal(422, error.StatusCode);
    }

    [Fact]
    public async Task FileConnector_CsvRoundTrip_PreservesRows()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-artifacts", Guid.NewGuid().ToString("N"));
        var connector = new FileConnector(directory);
        var data = JsonNode.Parse("""[{"id":"1","name":"Ada, Inc."},{"id":"2","name":"Grace"}]""")!;
        await connector.ExecuteAsync(
            "write",
            new JsonObject { ["path"] = "out/contacts.csv", ["data"] = data },
            new ConnectorExecutionContext("test"));
        var result = await connector.ExecuteAsync(
            "read",
            new JsonObject { ["path"] = "out/contacts.csv" },
            new ConnectorExecutionContext("test"));
        Assert.Equal(2, result.RecordsRead);
        Assert.Equal("Ada, Inc.", result.Payload![0]!["name"]!.GetValue<string>());
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task FileConnector_PathTraversal_IsRejected()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-artifacts", Guid.NewGuid().ToString("N"));
        var connector = new FileConnector(directory);
        await Assert.ThrowsAsync<ConnectorException>(() =>
            connector.ExecuteAsync(
                "read",
                new JsonObject { ["path"] = "..\\outside.json" },
                new ConnectorExecutionContext("test")));
        Directory.Delete(directory, true);
    }

    private static string Sign(byte[] body, string timestamp, string nonce, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.{nonce}.");
        return Convert.ToHexString(hmac.ComputeHash(prefix.Concat(body).ToArray())).ToLowerInvariant();
    }

    private static string ArtifactPath(string fileName)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-artifacts");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}-{fileName}");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
