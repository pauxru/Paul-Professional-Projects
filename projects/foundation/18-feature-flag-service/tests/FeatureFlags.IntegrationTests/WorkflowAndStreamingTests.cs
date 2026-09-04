using System.Net;
using System.Text.Json;
using FeatureFlags.Application;
using FeatureFlags.Domain;
using FeatureFlags.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureFlags.IntegrationTests;

public sealed class WorkflowAndStreamingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task AuditRevert_RestoresExactPriorEvaluationBehavior()
    {
        await factory.SeedAsync();
        var service = CreateServiceScope(out var scope);
        using (scope)
        {
            var environment = await service.GetEnvironmentAsync("acme", "dev", CancellationToken.None);
            var original = environment.Configuration.FindFlag("new-checkout")!;
            var enabled = original with { Rollout = null, FallthroughVariation = 1, IsOn = true };
            await service.SaveFlagAsync("acme", "dev", enabled, new ChangeContext("author", "enable fully"), true, CancellationToken.None);
            var disabled = enabled with { IsOn = false };
            await service.SaveFlagAsync("acme", "dev", disabled, new ChangeContext("author", "disable"), true, CancellationToken.None);
            var beforeRevert = await service.EvaluateAsync("acme", "dev", "new-checkout", EvaluationContext.Create("revert-user"), CancellationToken.None);
            Assert.False(beforeRevert.Value.GetBoolean());
            var audit = (await service.GetAuditAsync("acme", "dev", CancellationToken.None)).First(item => item.Action == "FlagSaved" && item.AfterJson.Contains("\"isOn\":false", StringComparison.Ordinal));
            await service.RevertAsync(audit.Id, new ChangeContext("operator", "restore prior behavior"), CancellationToken.None);
            var afterRevert = await service.EvaluateAsync("acme", "dev", "new-checkout", EvaluationContext.Create("revert-user"), CancellationToken.None);
            Assert.True(afterRevert.Value.GetBoolean());
        }
    }

    [Fact]
    public async Task ProductionApprovalWorkflow_EnforcesFourEyesBeforeApply()
    {
        await factory.SeedAsync();
        var service = CreateServiceScope(out var scope);
        using (scope)
        {
            var current = await service.GetEnvironmentAsync("acme", "production", CancellationToken.None);
            var proposed = current.Configuration.FindFlag("new-checkout")! with { IsOn = false };
            var request = await service.RequestFlagChangeAsync("acme", "production", proposed, new ChangeContext("maker", "CHG-18"), CancellationToken.None);
            await Assert.ThrowsAsync<FourEyesViolationException>(() => service.ReviewApprovalAsync(request.Id, "maker", true, null, CancellationToken.None));
            var approved = await service.ReviewApprovalAsync(request.Id, "reviewer", true, "reviewed", CancellationToken.None);
            Assert.Equal(ApprovalStatus.Approved, approved.Status);
            await service.ApplyApprovalAsync(request.Id, "release-manager", CancellationToken.None);
            var updated = await service.GetEnvironmentAsync("acme", "production", CancellationToken.None);
            Assert.False(updated.Configuration.FindFlag("new-checkout")!.IsOn);
        }
    }

    [Fact]
    public async Task DirectProductionChange_IsRejectedUntilApprovalWorkflowIsUsed()
    {
        await factory.SeedAsync();
        var service = CreateServiceScope(out var scope);
        using (scope)
        {
            var current = await service.GetEnvironmentAsync("acme", "production", CancellationToken.None);
            await Assert.ThrowsAsync<ApprovalRequiredException>(() => service.SaveFlagAsync("acme", "production", current.Configuration.FindFlag("new-checkout")!, new ChangeContext("maker"), false, CancellationToken.None));
        }
    }

    [Fact]
    public async Task KillSwitch_BypassesApprovalAndCreatesSeparatelyAuditedAction()
    {
        await factory.SeedAsync();
        var service = CreateServiceScope(out var scope);
        using (scope)
        {
            await service.SetKillSwitchAsync("acme", "production", "new-checkout", false, new ChangeContext("incident-operator", "incident"), CancellationToken.None);
            var audit = await service.GetAuditAsync("acme", "production", CancellationToken.None);
            Assert.Contains(audit, item => item.Action == "KillSwitchBypass");
        }
    }

    [Fact]
    public async Task StaleFlagReport_ListsFlagsWithNoRecentEvaluation()
    {
        await factory.SeedAsync();
        var service = CreateServiceScope(out var scope);
        using (scope)
        {
            var report = await service.GetStaleFlagsAsync("acme", "staging", 30, CancellationToken.None);
            Assert.Contains(report, item => item.FlagKey == "new-checkout" && item.Reason == "No recent evaluations");
        }
    }

    [Fact]
    public async Task PromotionPreviewAndApply_CopiesSourceRulesetToNonProductionTarget()
    {
        await factory.SeedAsync();
        var service = CreateServiceScope(out var scope);
        using (scope)
        {
            var source = await service.GetEnvironmentAsync("acme", "dev", CancellationToken.None);
            var changed = source.Configuration.FindFlag("pricing-copy")! with { FallthroughVariation = 1 };
            await service.SaveFlagAsync("acme", "dev", changed, new ChangeContext("operator"), true, CancellationToken.None);
            var preview = await service.PreviewPromotionAsync("acme", "dev", "staging", CancellationToken.None);
            Assert.NotEmpty(preview.Differences);
            await service.PromoteAsync("acme", "dev", "staging", new ChangeContext("operator"), false, CancellationToken.None);
            var target = await service.GetEnvironmentAsync("acme", "staging", CancellationToken.None);
            Assert.Equal(1, target.Configuration.FindFlag("pricing-copy")!.FallthroughVariation);
        }
    }

    [Fact]
    public async Task SseChange_RefreshesConnectedSdkLocalRuleset()
    {
        await factory.SeedAsync();
        using var http = factory.CreateClient();
        var cache = Path.Combine(AppContext.BaseDirectory, $"sse-{Guid.NewGuid():N}.json");
        try
        {
            var options = new FeatureFlags.Sdk.FeatureFlagClientOptions
            {
                ApiBaseUrl = http.BaseAddress!.ToString(), ProjectKey = "acme", EnvironmentKey = "dev", SdkKey = "client-dev-acme-public-demo",
                PollIntervalSeconds = 3600, FlushIntervalSeconds = 3600, EventBufferCapacity = 100, OfflineStoragePath = cache
            };
            await using var sdk = new FeatureFlags.Sdk.FeatureFlagClient(http, options);
            await sdk.InitializeAsync();
            var service = CreateServiceScope(out var scope);
            using (scope)
            {
                var current = await service.GetEnvironmentAsync("acme", "dev", CancellationToken.None);
                var update = current.Configuration.FindFlag("new-checkout")! with { IsOn = false };
                await Task.Delay(150);
                await service.SaveFlagAsync("acme", "dev", update, new ChangeContext("operator"), true, CancellationToken.None);
            }

            var reflected = false;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (!sdk.BoolVariation("new-checkout", EvaluationContext.Create("sse-user"), true))
                {
                    reflected = true;
                    break;
                }
                await Task.Delay(100);
            }
            Assert.True(reflected, "SDK did not reflect the SSE configuration change within four seconds.");
        }
        finally
        {
            if (File.Exists(cache)) File.Delete(cache);
        }
    }

    private FlagService CreateServiceScope(out IServiceScope scope)
    {
        scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<FlagService>();
    }
}
