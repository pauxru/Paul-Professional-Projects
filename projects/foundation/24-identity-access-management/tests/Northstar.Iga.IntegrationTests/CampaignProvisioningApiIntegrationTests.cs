using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Northstar.Iga.Application;
using Northstar.Iga.Domain;
using Northstar.Iga.Infrastructure;

namespace Northstar.Iga.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class CampaignProvisioningApiIntegrationTests(ApiFactory factory)
{
    private const string Actor = "integration-test";
    private const string Correlation = "campaign-provisioning-correlation";

    [Fact]
    public async Task Campaign_ApplicationScope_GeneratesItemPerUserEntitlement()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        await service.GrantEntitlementAsync(
            data.User.Id, data.Medium.Id, GrantSource.Direct, "Current access", null, Actor, Correlation, default);

        var campaign = await service.CreateCampaignAsync(new(
            "Finance quarterly review",
            "application",
            data.Finance.Key,
            ReviewerMode.EntitlementOwner,
            factory.Clock.UtcNow.AddDays(7)), Actor, Correlation, default);
        var items = await service.GetCampaignItemsAsync(campaign.Id, default);

        var item = Assert.Single(items);
        Assert.Equal(data.User.Id, item.UserId);
        Assert.Equal(data.Medium.Id, item.EntitlementId);
        Assert.Equal(data.OwnerOne.Id, item.ReviewerId);
        Assert.Contains("direct-entitlement", item.DerivationJson);
    }

    [Fact]
    public async Task Campaign_BulkApprove_UpdatesProgressAndCompletesCampaign()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, db, data) = await ServicesAsync(scope);
        var second = TestData.Entitlement(
            data.Finance.Id, "vendor-comment", "app:finance/vendor:comment", data.OwnerOne.Id, RiskRating.Low);
        db.Entitlements.Add(second);
        await db.SaveChangesAsync();
        await service.GrantEntitlementAsync(
            data.User.Id, data.Low.Id, GrantSource.Direct, "Read access", null, Actor, Correlation, default);
        await service.GrantEntitlementAsync(
            data.User.Id, second.Id, GrantSource.Direct, "Comment access", null, Actor, Correlation, default);
        var campaign = await service.CreateCampaignAsync(new(
            "Finance bulk review", "application", "finance", ReviewerMode.EntitlementOwner,
            factory.Clock.UtcNow.AddDays(7)), Actor, Correlation, default);
        var items = await service.GetCampaignItemsAsync(campaign.Id, default);

        var progress = await service.CertifyItemsAsync(campaign.Id, new(
            items.Select(x => x.Id).ToArray(),
            CertificationDecision.Approved,
            "Both entitlements remain appropriate",
            data.OwnerOne.Id), Actor, Correlation, default);

        Assert.Equal(2, progress.Approved);
        Assert.Equal(0, progress.Pending);
        Assert.Equal(100m, progress.CompletionPercent);
        Assert.Equal(CampaignStatus.Completed,
            (await service.GetCampaignsAsync(default)).Single(x => x.Id == campaign.Id).Status);
    }

    [Fact]
    public async Task Campaign_ReviewerRevocation_RemovesEffectivePermission()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        await service.GrantEntitlementAsync(
            data.User.Id, data.Medium.Id, GrantSource.Direct, "Current access", null, Actor, Correlation, default);
        var campaign = await service.CreateCampaignAsync(new(
            "Finance revoke review", "application", "finance", ReviewerMode.EntitlementOwner,
            factory.Clock.UtcNow.AddDays(7)), Actor, Correlation, default);
        var item = Assert.Single(await service.GetCampaignItemsAsync(campaign.Id, default));

        await service.CertifyItemsAsync(campaign.Id, new(
            [item.Id],
            CertificationDecision.Revoked,
            "Role changed and access is no longer required",
            data.OwnerOne.Id), Actor, Correlation, default);
        var decision = await service.EvaluateAsync(
            new(data.User.Id, data.Medium.Permission, null, null), Actor, Correlation, default);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Campaign_Deadline_AutoRevokesPendingItems()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        await service.GrantEntitlementAsync(
            data.User.Id, data.Medium.Id, GrantSource.Direct, "Uncertified access", null, Actor, Correlation, default);
        var campaign = await service.CreateCampaignAsync(new(
            "Short deadline review", "application", "finance", ReviewerMode.EntitlementOwner,
            factory.Clock.UtcNow.AddHours(1)), Actor, Correlation, default);
        factory.Clock.Advance(TimeSpan.FromHours(2));

        var count = await service.AutoRevokeOverdueCampaignsAsync(Actor, Correlation, default);
        var item = Assert.Single(await service.GetCampaignItemsAsync(campaign.Id, default));
        var decision = await service.EvaluateAsync(
            new(data.User.Id, data.Medium.Permission, null, null), Actor, Correlation, default);

        Assert.Equal(1, count);
        Assert.Equal(CertificationDecision.AutoRevoked, item.Decision);
        Assert.Equal(CampaignStatus.Overdue,
            (await service.GetCampaignsAsync(default)).Single(x => x.Id == campaign.Id).Status);
        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Provisioning_Create_CreatesEnabledTargetAccountWithExpectedGrant()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        var control = scope.ServiceProvider.GetRequiredService<ISimulatedConnectorControl>();
        await service.GrantEntitlementAsync(
            data.User.Id, data.Low.Id, GrantSource.Direct, "Target access", null, Actor, Correlation, default);

        var result = await service.ProvisionUserAsync(
            data.User.Id, "finance", ProvisioningOperation.Create, Actor, Correlation, default);
        var account = Assert.Single(control.Snapshot("finance"));

        Assert.Equal(ProvisioningJobStatus.Succeeded, result.Status);
        Assert.True(account.Enabled);
        Assert.Contains(data.Low.Permission, account.Permissions);
    }

    [Fact]
    public async Task Provisioning_Disable_DisablesExistingTargetAccount()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        var control = scope.ServiceProvider.GetRequiredService<ISimulatedConnectorControl>();
        await service.ProvisionUserAsync(
            data.User.Id, "finance", ProvisioningOperation.Create, Actor, Correlation, default);

        var result = await service.ProvisionUserAsync(
            data.User.Id, "finance", ProvisioningOperation.Disable, Actor, Correlation, default);

        Assert.Equal(ProvisioningJobStatus.Succeeded, result.Status);
        Assert.False(Assert.Single(control.Snapshot("finance")).Enabled);
    }

    [Fact]
    public async Task Provisioning_TransientFailures_RetriesThenSucceeds()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        var control = scope.ServiceProvider.GetRequiredService<ISimulatedConnectorControl>();
        control.FailNext("finance", ProvisioningOperation.Create, 2);

        var result = await service.ProvisionUserAsync(
            data.User.Id, "finance", ProvisioningOperation.Create, Actor, Correlation, default);

        Assert.Equal(ProvisioningJobStatus.Succeeded, result.Status);
        Assert.Equal(3, result.Attempts);
    }

    [Fact]
    public async Task Provisioning_ExhaustedRetries_MovesJobToQuarantine()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        var control = scope.ServiceProvider.GetRequiredService<ISimulatedConnectorControl>();
        control.FailNext("finance", ProvisioningOperation.Create, 3);

        var result = await service.ProvisionUserAsync(
            data.User.Id, "finance", ProvisioningOperation.Create, Actor, Correlation, default);
        var quarantine = await service.GetQuarantineAsync(default);

        Assert.Equal(ProvisioningJobStatus.Quarantined, result.Status);
        Assert.Equal(3, result.Attempts);
        Assert.Single(quarantine);
        Assert.Equal(result.JobId, quarantine[0].JobId);
    }

    [Fact]
    public async Task Reconciliation_DetectsOrphanAccountAndRogueGrant()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        var control = scope.ServiceProvider.GetRequiredService<ISimulatedConnectorControl>();
        await service.GrantEntitlementAsync(
            data.User.Id, data.Low.Id, GrantSource.Direct, "Expected access", null, Actor, Correlation, default);
        control.SeedAccount("finance", new(
            "finance-known",
            data.User.Id,
            data.User.Email,
            true,
            [data.Low.Permission, "app:finance/shadow-admin"]));
        control.SeedAccount("finance", new(
            "finance-orphan",
            Guid.NewGuid(),
            "orphan@target.example",
            true,
            []));

        var result = await service.ReconcileAsync("finance", Actor, Correlation, default);

        Assert.Contains(result.Orphans, x => x.ExternalId == "finance-orphan");
        Assert.Contains(result.RogueGrants, x =>
            x.ExternalId == "finance-known" && x.Detail.Contains("shadow-admin", StringComparison.Ordinal));
        Assert.Single(await service.GetOrphanAccountsReportAsync(default));
    }

    [Fact]
    public async Task Audit_ModificationAttempt_IsRejectedByAppendOnlyGuard()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, db, data) = await ServicesAsync(scope);
        await service.GrantEntitlementAsync(
            data.User.Id, data.Low.Id, GrantSource.Direct, "Audited grant", null, Actor, Correlation, default);
        var audit = await db.AuditRecords.FirstAsync();
        audit.Action = "tampered";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", exception.Message);
    }

    [Fact]
    public async Task Api_ProtectedEndpointWithoutToken_Returns401()
    {
        await factory.ResetAsync();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_ReadTokenOnAdminEndpoint_Returns403()
    {
        await factory.ResetAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetTokenAsync(client, "iga.read"));
        var response = await client.PostAsJsonAsync("/api/v1/users", ValidCreateUserPayload());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Api_ApprovalActorDifferentFromJwtSubject_Returns403()
    {
        await factory.ResetAsync();
        Guid requestId;
        Guid stepId;
        Guid assertedManagerId;
        Guid differentSubjectId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var (service, _, data) = await ServicesAsync(scope);
            var request = await service.CreateAccessRequestAsync(new(
                data.User.Id, data.User.Id, RequestTargetType.Entitlement, data.Medium.Id,
                "Need approval actor binding checked", 30), Actor, Correlation, default);
            var step = Assert.Single(await service.GetApprovalStepsAsync(request.Id, default), x => x.Stage == 1);
            requestId = request.Id;
            stepId = step.Id;
            assertedManagerId = data.Manager.Id;
            differentSubjectId = data.OwnerTwo.Id;
        }
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetTokenAsync(client, "iga.approve", differentSubjectId.ToString()));

        var response = await client.PostAsJsonAsync(
            $"/api/v1/requests/{requestId}/steps/{stepId}/approve",
            new { actorId = assertedManagerId, reason = "Spoofed manager approval" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Api_InvalidUserPayload_ReturnsValidationProblemDetails()
    {
        await factory.ResetAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetTokenAsync(client, "iga.admin"));
        var response = await client.PostAsJsonAsync("/api/v1/users", new
        {
            employeeNumber = "",
            displayName = "",
            email = "",
            department = "Finance",
            jobTitle = "Analyst",
            location = "Nairobi",
            costCentre = "CC-FIN",
            employmentType = "Employee",
            clearance = 2,
            startDate = "2026-09-03"
        });
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("errors", out _));
        Assert.True(document.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Api_AuthorizationEvaluation_ReturnsDecisionExplanationAndCorrelationId()
    {
        await factory.ResetAsync();
        Guid userId;
        string permission;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var (service, _, data) = await ServicesAsync(scope);
            await service.GrantEntitlementAsync(
                data.User.Id, data.Medium.Id, GrantSource.Direct, "API access", null, Actor, Correlation, default);
            userId = data.User.Id;
            permission = data.Medium.Permission;
        }
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetTokenAsync(client, "iga.read"));
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "api-correlation-123");

        var response = await client.PostAsJsonAsync("/api/v1/authz/evaluate", new
        {
            userId,
            permission,
            resource = new { classification = "Internal" },
            environment = new { networkZone = "Corporate", deviceTrust = "Trusted", mfaLevel = 2 }
        });
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(document.RootElement.GetProperty("allowed").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("explanation").GetString()));
        Assert.Equal("api-correlation-123", response.Headers.GetValues("X-Correlation-Id").Single());
    }

    private static async Task<(IIgaService Service, IgaDbContext Db, SeedData Data)> ServicesAsync(
        AsyncServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<IgaDbContext>();
        var data = await TestData.SeedAsync(db);
        return (scope.ServiceProvider.GetRequiredService<IIgaService>(), db, data);
    }

    private static async Task<string> GetTokenAsync(HttpClient client, params string[] scopes) =>
        await GetTokenAsync(client, scopes, "integration-api");

    private static async Task<string> GetTokenAsync(HttpClient client, string scope, string subject) =>
        await GetTokenAsync(client, [scope], subject);

    private static async Task<string> GetTokenAsync(HttpClient client, string[] scopes, string subject)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            subject,
            scopes
        });
        response.EnsureSuccessStatusCode();
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static object ValidCreateUserPayload() => new
    {
        employeeNumber = "API-100",
        displayName = "API User",
        email = "api.user@northstar.test",
        department = "Finance",
        jobTitle = "Analyst",
        managerId = (Guid?)null,
        location = "Nairobi",
        costCentre = "CC-FIN",
        employmentType = "Employee",
        clearance = 2,
        startDate = "2026-09-03",
        endDate = (string?)null
    };
}
