using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;

namespace Northstar.Secrets.UnitTests;

public sealed class RotationEngineTests
{
    [Fact]
    public async Task DualWriteRotation_WithConsumerAcknowledgement_CompletesAndKeepsPrevious()
    {
        await using var harness = await TestHarness.CreateAsync();
        var consumer = await harness.AddConsumerAsync();
        await harness.RegisterAsync(consumerIds: [consumer.Id]);
        var rotation = await harness.Engine.RequestAsync(
            Request("orders/prod/database", RotationStrategyKind.DualWrite, "dual-happy"),
            CancellationToken.None);

        rotation = await harness.Engine.RunToPauseOrTerminalAsync(rotation.Id, CancellationToken.None);
        Assert.Equal(RotationState.AwaitingAcknowledgement, rotation.State);
        Assert.Single(harness.Notifications.Notices);

        await harness.Engine.AcknowledgeAsync(rotation.Id, consumer.Id, CancellationToken.None);
        rotation = await harness.Engine.RunToPauseOrTerminalAsync(rotation.Id, CancellationToken.None);
        var secret = await harness.Repository.GetSecretAsync(
            "orders/prod/database", CancellationToken.None);

        Assert.Equal(RotationState.Completed, rotation.State);
        Assert.Equal(2, secret!.CurrentVersion!.VersionNumber);
        Assert.Equal(SecretVersionState.Previous, secret.Versions.Single(x => x.VersionNumber == 1).State);
    }

    [Fact]
    public async Task SingleCutover_AtMaintenanceWindow_CompletesWithoutRequiredAcknowledgement()
    {
        await using var harness = await TestHarness.CreateAsync();
        var consumer = await harness.AddConsumerAsync();
        await harness.RegisterAsync(consumerIds: [consumer.Id]);
        var rotation = await harness.Engine.RequestAsync(
            Request(
                "orders/prod/database",
                RotationStrategyKind.SingleCutover,
                "cutover-happy",
                harness.Clock.UtcNow),
            CancellationToken.None);

        rotation = await harness.Engine.RunToPauseOrTerminalAsync(rotation.Id, CancellationToken.None);

        Assert.Equal(RotationState.Completed, rotation.State);
        Assert.All(rotation.Acknowledgements,
            x => Assert.Equal(ConsumerAcknowledgementStatus.NotRequired, x.Status));
    }

    [Fact]
    public async Task VerificationFailure_AutomaticallyRollsBackAndRestoresPrevious()
    {
        await using var harness = await TestHarness.CreateAsync();
        await harness.RegisterAsync();
        harness.Verifier.SetFailure("orders/prod/database", true);
        var rotation = await harness.Engine.RequestAsync(
            Request("orders/prod/database", RotationStrategyKind.DualWrite, "verify-fail"),
            CancellationToken.None);

        rotation = await harness.Engine.RunToPauseOrTerminalAsync(rotation.Id, CancellationToken.None);
        var secret = await harness.Repository.GetSecretAsync(
            "orders/prod/database", CancellationToken.None);

        Assert.Equal(RotationState.RolledBack, rotation.State);
        Assert.Equal(1, secret!.CurrentVersion!.VersionNumber);
        Assert.Equal(SecretVersionState.Revoked, secret.Versions.Single(x => x.VersionNumber == 2).State);
    }

    [Fact]
    public async Task AcknowledgementTimeout_AutomaticallyRollsBackCandidate()
    {
        await using var harness = await TestHarness.CreateAsync();
        var consumer = await harness.AddConsumerAsync();
        await harness.RegisterAsync(consumerIds: [consumer.Id]);
        var rotation = await harness.Engine.RequestAsync(
            Request("orders/prod/database", RotationStrategyKind.DualWrite, "ack-timeout"),
            CancellationToken.None);
        rotation = await harness.Engine.RunToPauseOrTerminalAsync(rotation.Id, CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromMinutes(11));

        rotation = await harness.Engine.RunToPauseOrTerminalAsync(rotation.Id, CancellationToken.None);
        var secret = await harness.Repository.GetSecretAsync(
            "orders/prod/database", CancellationToken.None);

        Assert.Equal(RotationState.RolledBack, rotation.State);
        Assert.Equal(ConsumerAcknowledgementStatus.TimedOut, rotation.Acknowledgements.Single().Status);
        Assert.Equal(1, secret!.CurrentVersion!.VersionNumber);
    }

    [Fact]
    public async Task RotationInterruptedAfterRequestedStep_ResumesFromPersistedGeneratingState()
    {
        await using var harness = await TestHarness.CreateAsync();
        await harness.RegisterAsync();
        var rotation = await harness.Engine.RequestAsync(
            Request("orders/prod/database", RotationStrategyKind.DualWrite, "crash-resume"),
            CancellationToken.None);
        rotation = await harness.Engine.AdvanceOneStepAsync(rotation.Id, CancellationToken.None);
        Assert.Equal(RotationState.Generating, rotation.State);
        harness.Db.ChangeTracker.Clear();

        var restartedEngine = harness.CreateEngine();
        rotation = await restartedEngine.RunToPauseOrTerminalAsync(rotation.Id, CancellationToken.None);

        Assert.Equal(RotationState.Completed, rotation.State);
        Assert.Equal(2, rotation.NewVersionNumber);
    }

    [Fact]
    public async Task Request_WithRepeatedIdempotencyKey_ReturnsOriginalRotation()
    {
        await using var harness = await TestHarness.CreateAsync();
        await harness.RegisterAsync();
        var first = await harness.Engine.RequestAsync(
            Request("orders/prod/database", RotationStrategyKind.DualWrite, "same-key"),
            CancellationToken.None);
        var second = await harness.Engine.RequestAsync(
            Request("orders/prod/database", RotationStrategyKind.DualWrite, "same-key"),
            CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task ManualRollback_AfterCompletion_RestoresPreviousVersion()
    {
        await using var harness = await TestHarness.CreateAsync();
        await harness.RegisterAsync();
        var rotation = await harness.Engine.RequestAsync(
            Request("orders/prod/database", RotationStrategyKind.DualWrite, "manual-rollback"),
            CancellationToken.None);
        rotation = await harness.Engine.RunToPauseOrTerminalAsync(rotation.Id, CancellationToken.None);
        Assert.Equal(RotationState.Completed, rotation.State);

        rotation = await harness.Engine.RollbackAsync(
            rotation.Id, "Post-cutover regression.", CancellationToken.None);
        var secret = await harness.Repository.GetSecretAsync(
            "orders/prod/database", CancellationToken.None);

        Assert.Equal(RotationState.RolledBack, rotation.State);
        Assert.Equal(1, secret!.CurrentVersion!.VersionNumber);
        Assert.Equal(SecretVersionState.Revoked, secret.Versions.Single(x => x.VersionNumber == 2).State);
    }

    private static RequestRotationCommand Request(
        string name,
        RotationStrategyKind strategy,
        string idempotencyKey,
        DateTimeOffset? maintenanceWindow = null) =>
        new(
            name,
            strategy,
            idempotencyKey,
            "operator",
            Guid.NewGuid().ToString("N"),
            maintenanceWindow);
}
