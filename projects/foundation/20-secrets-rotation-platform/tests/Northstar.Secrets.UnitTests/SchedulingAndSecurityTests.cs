using Microsoft.Extensions.Logging;
using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;
using Northstar.Secrets.Infrastructure.Security;

namespace Northstar.Secrets.UnitTests;

public sealed class SchedulingAndSecurityTests
{
    [Fact]
    public void ScheduleCalculator_ForTwoHundredSecrets_DistributesJitter()
    {
        var calculator = new RotationScheduleCalculator();
        var now = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
        var secrets = Enumerable.Range(1, 200).Select(index =>
        {
            var secret = SecretRecord.Register(
                Guid.NewGuid(),
                $"app-{index:000}/prod/api",
                SecretType.ApiKey,
                "Northstar Platform Engineering (fictional)",
                "prod",
                SecretCriticality.High,
                ["synthetic"],
                "test",
                TimeSpan.FromDays(1),
                TimeSpan.FromDays(30),
                TimeSpan.FromHours(1),
                now);
            var version = secret.StageVersion(
                [1], new byte[12], new byte[16], new byte[60], "v1", now, now.AddDays(30));
            secret.Promote(version.VersionNumber, now);
            return secret;
        }).ToArray();

        var schedule = calculator.BuildDistribution(secrets);

        Assert.True(schedule.Distinct().Count() > 190);
        Assert.True(schedule[^1] - schedule[0] > TimeSpan.FromHours(3));
        Assert.All(schedule, due =>
            Assert.InRange(due, now.AddHours(21.5), now.AddHours(26.5)));
    }

    [Fact]
    public async Task ExpiryReport_WithinHorizon_ReturnsCurrentVersion()
    {
        await using var harness = await TestHarness.CreateAsync();
        await harness.RegisterAsync(maxAgeHours: 48);
        var reporting = new ReportingService(harness.Repository, harness.Clock);

        var report = await reporting.UpcomingExpiryAsync(
            TimeSpan.FromHours(72), CancellationToken.None);

        var item = Assert.Single(report);
        Assert.Equal("orders/prod/database", item.Name);
        Assert.Equal(1, item.Version);
        Assert.False(item.Overdue);
    }

    [Fact]
    public async Task AutomaticScheduler_WhenDue_RequestsAndCompletesRotation()
    {
        await using var harness = await TestHarness.CreateAsync();
        await harness.RegisterAsync(rotationHours: 24);
        harness.Clock.Advance(TimeSpan.FromHours(28));
        var scheduler = new AutomaticRotationScheduler(
            harness.Repository,
            new RotationScheduleCalculator(),
            harness.Engine,
            harness.Clock);

        var rotations = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.Single(rotations);
        var operation = await harness.Engine.GetAsync(rotations[0], CancellationToken.None);
        Assert.Equal(RotationState.Completed, operation.State);
    }

    [Fact]
    public void PathPolicy_WithSegmentWildcards_AllowsMatchingPath()
    {
        var policy = new AccessPolicy(
            Guid.NewGuid(), "reader", "orders/*/database", false, true, false, false);

        Assert.True(new PathPolicyEvaluator().IsAllowed(
            [policy], "reader", "orders/prod/database", SecretPermission.ReadValue));
    }

    [Fact]
    public void PathPolicy_WithDifferentPrefix_DeniesPath()
    {
        var policy = new AccessPolicy(
            Guid.NewGuid(), "reader", "billing/**", false, true, false, false);

        Assert.False(new PathPolicyEvaluator().IsAllowed(
            [policy], "reader", "orders/prod/database", SecretPermission.ReadValue));
    }

    [Fact]
    public void PathPolicy_WithPartialSegmentGlob_MatchesApplicationFleet()
    {
        var policy = new AccessPolicy(
            Guid.NewGuid(), "reader", "synthetic-app-*/prod/*", false, true, false, false);

        Assert.True(new PathPolicyEvaluator().IsAllowed(
            [policy], "reader", "synthetic-app-20/prod/api", SecretPermission.ReadValue));
    }

    [Fact]
    public async Task ReadValue_AlwaysAuditsActorReasonAndCorrelationId()
    {
        await using var harness = await TestHarness.CreateAsync();
        await harness.RegisterAsync();
        await harness.AddReadPolicyAsync("service:orders", "orders/**");
        var access = new AccessContext(
            "service:orders", "refresh after rotation", "correlation-123", "127.0.0.1");

        var result = await harness.Lifecycle.ReadValueAsync(
            "orders/prod/database", null, access, CancellationToken.None);
        var secret = await harness.Repository.GetSecretAsync(
            "orders/prod/database", CancellationToken.None);
        var audit = Assert.Single(await harness.Repository.ListAuditsAsync(
            secret!.Id, null, CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(result.Value));
        Assert.Equal("service:orders", audit.Actor);
        Assert.Equal("refresh after rotation", audit.Reason);
        Assert.Equal("correlation-123", audit.CorrelationId);
        Assert.Equal("allowed", audit.Outcome);
    }

    [Fact]
    public async Task ReadValue_WithDeniedPath_AuditsDeniedAttempt()
    {
        await using var harness = await TestHarness.CreateAsync();
        await harness.RegisterAsync();
        await harness.AddReadPolicyAsync("service:billing", "billing/**");

        await Assert.ThrowsAsync<ForbiddenOperationException>(() =>
            harness.Lifecycle.ReadValueAsync(
                "orders/prod/database",
                null,
                new AccessContext("service:billing", "attempt", "correlation-denied"),
                CancellationToken.None));
        var records = await harness.Repository.ListAuditsAsync(
            null, null, CancellationToken.None);
        Assert.Contains(records, x =>
            x.CorrelationId == "correlation-denied" && x.Outcome == "denied");
    }

    [Fact]
    public async Task EmergencyRevoke_WithFourEyesApproval_InvalidatesAllVersions()
    {
        await using var harness = await TestHarness.CreateAsync();
        await harness.RegisterAsync();
        var approvals = new FourEyesService(harness.Repository, harness.Clock);
        var service = new BreakGlassService(
            harness.Repository, harness.Lifecycle, approvals, harness.Engine, harness.Clock);
        var approval = await service.RequestAsync(
            ApprovalOperation.EmergencyRevoke,
            "orders/prod/database",
            "responder-a",
            "synthetic incident",
            CancellationToken.None);
        await service.ApproveAsync(approval.Id, "responder-b", CancellationToken.None);

        await service.EmergencyRevokeAsync(
            approval.Id,
            "orders/prod/database",
            new AccessContext("responder-a", "synthetic incident", "incident-1"),
            CancellationToken.None);
        var secret = await harness.Repository.GetSecretAsync(
            "orders/prod/database", CancellationToken.None);

        Assert.All(secret!.Versions,
            x => Assert.Equal(SecretVersionState.Revoked, x.State));
    }

    [Fact]
    public async Task FourEyesApproval_WhenRequesterApprovesOwnRequest_IsRejected()
    {
        await using var harness = await TestHarness.CreateAsync();
        var approvals = new FourEyesService(harness.Repository, harness.Clock);
        var request = await approvals.RequestAsync(
            ApprovalOperation.BreakGlassRead,
            "orders/prod/database",
            "same-actor",
            "diagnostic",
            CancellationToken.None);

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            approvals.ApproveAsync(request.Id, "same-actor", CancellationToken.None));
    }

    [Fact]
    public async Task DestroyVersion_WithoutSecondApproval_IsRejected()
    {
        await using var harness = await TestHarness.CreateAsync();
        await harness.RegisterAsync();
        var rotation = await harness.Engine.RequestAsync(
            new RequestRotationCommand(
                "orders/prod/database",
                RotationStrategyKind.DualWrite,
                "prepare-destroy",
                "operator",
                "correlation"),
            CancellationToken.None);
        await harness.Engine.RunToPauseOrTerminalAsync(rotation.Id, CancellationToken.None);
        var approvals = new FourEyesService(harness.Repository, harness.Clock);
        var service = new BreakGlassService(
            harness.Repository, harness.Lifecycle, approvals, harness.Engine, harness.Clock);
        var approval = await service.RequestAsync(
            ApprovalOperation.DestroyVersion,
            "orders/prod/database#v1",
            "requester",
            "retention elapsed",
            CancellationToken.None);

        await Assert.ThrowsAsync<ForbiddenOperationException>(() =>
            service.DestroyVersionAsync(
                approval.Id,
                "orders/prod/database",
                1,
                new AccessContext("requester", "retention elapsed", "destroy-1"),
                CancellationToken.None));
    }

    [Fact]
    public async Task BreakGlassRead_WithSecondApproval_ReturnsValueAndWritesAudit()
    {
        await using var harness = await TestHarness.CreateAsync();
        await harness.RegisterAsync();
        var approvals = new FourEyesService(harness.Repository, harness.Clock);
        var service = new BreakGlassService(
            harness.Repository, harness.Lifecycle, approvals, harness.Engine, harness.Clock);
        var approval = await service.RequestAsync(
            ApprovalOperation.BreakGlassRead,
            "orders/prod/database",
            "responder-a",
            "production diagnostic",
            CancellationToken.None);
        await service.ApproveAsync(approval.Id, "responder-b", CancellationToken.None);

        var result = await service.ReadAsync(
            approval.Id,
            "orders/prod/database",
            null,
            new AccessContext("responder-a", "production diagnostic", "break-glass-1"),
            CancellationToken.None);
        var audits = await harness.Repository.ListAuditsAsync(
            null, null, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.Value));
        Assert.Contains(audits, x => x.Action == "break-glass.read");
        Assert.Contains(audits, x => x.Action == "secret.read-value");
    }

    [Fact]
    public async Task AnomalyReport_DetectsFirstTimeOutsideHoursAndReadSpike()
    {
        await using var harness = await TestHarness.CreateAsync();
        await harness.RegisterAsync();
        var secret = await harness.Repository.GetSecretAsync(
            "orders/prod/database", CancellationToken.None);
        var atNight = new DateTimeOffset(2026, 9, 3, 2, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 5; index++)
        {
            await harness.Repository.AddAuditAsync(
                new AuditRecord(
                    Guid.NewGuid(),
                    secret!.Id,
                    "unfamiliar-reader",
                    "secret.read-value",
                    secret.Name,
                    "synthetic analysis",
                    $"correlation-{index}",
                    atNight.AddMinutes(index),
                    "allowed"),
                CancellationToken.None);
        }

        await harness.Repository.SaveChangesAsync(CancellationToken.None);
        var report = await new ReportingService(harness.Repository, harness.Clock)
            .AccessAnomaliesAsync(atNight.AddHours(-1), CancellationToken.None);

        Assert.Contains(report, x => x.Type == "FirstTimeReader");
        Assert.Contains(report, x => x.Type == "UnusualReader");
        Assert.Contains(report, x => x.Type == "OutsideBusinessHours");
        Assert.Contains(report, x => x.Type == "ReadSpike");
    }

    [Fact]
    public void RedactingLogger_WhenSecretAppearsInStateAndException_RemovesEveryOccurrence()
    {
        const string value = "runtime-super-sensitive-value";
        var registry = new SecretRedactionRegistry();
        registry.Register(value);
        var sink = new InMemoryRedactedLogSink();
        using var provider = new RedactingLoggerProvider(registry, sink);
        var logger = provider.CreateLogger("test");

        logger.LogError(new InvalidOperationException($"failed with {value}"),
            "Accidental structured value {Value}", value);
        var output = string.Join(Environment.NewLine, sink.Messages);

        Assert.DoesNotContain(value, output, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", output);
    }
}
