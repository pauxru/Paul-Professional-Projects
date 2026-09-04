using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using IntegrationHub.Application;
using IntegrationHub.Domain;
using IntegrationHub.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IntegrationHub.Api;

public sealed class ScheduledFlowWorker(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<ScheduledFlowWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, FlowScheduleState> _states = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduled flow scan failed");
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFlowStore>();
        var parser = scope.ServiceProvider.GetRequiredService<IFlowDefinitionParser>();
        var runner = scope.ServiceProvider.GetRequiredService<IFlowRunner>();
        var scheduler = new FlowScheduler(clock);

        foreach (var flow in await store.ListAsync(cancellationToken))
        {
            if (flow.ActiveVersion is not int active)
            {
                continue;
            }
            var version = flow.Versions.Single(x => x.Version == active);
            var definition = parser.Parse(version.Format, version.Definition);
            if (definition.Trigger.Kind != FlowTriggerKind.Schedule || definition.Trigger.Cron is null)
            {
                continue;
            }

            var state = _states.GetOrAdd(flow.Id, _ => new FlowScheduleState
            {
                LastScheduledAt = clock.UtcNow.AddMinutes(-1)
            });
            var decision = scheduler.Evaluate(
                CronExpression.Parse(definition.Trigger.Cron),
                state,
                definition.Trigger.CatchUpPolicy,
                TimeSpan.FromMinutes(10));
            if (!decision.ShouldRun)
            {
                continue;
            }

            state.IsRunning = true;
            state.LastScheduledAt = decision.NextDue;
            try
            {
                await runner.RunAsync(flow.Id, new JsonObject { ["scheduledAt"] = decision.NextDue }, Guid.NewGuid().ToString("N"), cancellationToken);
            }
            finally
            {
                state.IsRunning = false;
            }
        }
    }
}

public sealed class HistoryRetentionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<RetentionOptions> retention,
    IClock clock,
    ILogger<HistoryRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PurgeAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PurgeAsync(stoppingToken);
        }
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IExecutionStore>()
                .PurgeOlderThanAsync(clock.UtcNow.AddDays(-retention.Value.RunHistoryDays), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Run history retention failed");
        }
    }
}

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, IWebHostEnvironment environment)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationHubDbContext>();
        await db.Database.EnsureCreatedAsync();
        if (!environment.IsDevelopment())
        {
            return;
        }

        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        var existing = await secrets.ListAsync();
        var demoSecrets = new Dictionary<string, string>
        {
            ["crm/apiKey"] = "dev-only-simulator-key",
            ["erp/username"] = "demo",
            ["erp/password"] = "dev-only-simulator-password",
            ["payments/clientId"] = "demo-client",
            ["payments/clientSecret"] = "dev-only-simulator-secret",
            ["webhooks/signingKey"] = "dev-only-webhook-signing-key"
        };
        foreach (var item in demoSecrets.Where(x => existing.All(s => s.Name != x.Key)))
        {
            await secrets.SetAsync(item.Key, item.Value);
        }

        if (await db.Flows.AnyAsync())
        {
            return;
        }
        var parser = scope.ServiceProvider.GetRequiredService<IFlowDefinitionParser>();
        var definition = new FlowDefinition(
            "CRM contacts to ERP customers",
            new TriggerDefinition(FlowTriggerKind.Manual),
            [
                new FlowStepDefinition("fetch-crm", FlowStepKind.Fetch, new Dictionary<string, string>
                {
                    ["connectorId"] = "contoso-crm",
                    ["operation"] = "contacts.list",
                    ["pageSize"] = "2"
                }),
                new FlowStepDefinition("map-customer", FlowStepKind.Transform, new Dictionary<string, string>
                {
                    ["mappings"] = """
                        [
                          {"targetPath":"$.id","expression":"$.id"},
                          {"targetPath":"$.name","expression":"trim($.displayName)"},
                          {"targetPath":"$.currency","expression":"currency(default($.currency, 'KES'))"}
                        ]
                        """
                }),
                new FlowStepDefinition("load-erp", FlowStepKind.Load, new Dictionary<string, string>
                {
                    ["connectorId"] = "acme-erp",
                    ["operation"] = "customers.upsert",
                    ["batchKey"] = "crm-to-erp"
                })
            ]);
        var flowStore = scope.ServiceProvider.GetRequiredService<IFlowStore>();
        var flow = await flowStore.CreateAsync("CRM contacts to ERP customers", "json", parser.Serialize("json", definition), "seed", CancellationToken.None);
        await flowStore.ActivateAsync(flow.Id, 1, CancellationToken.None);
    }
}

public sealed class DatabaseHealthCheck(
    IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var canConnect = await scope.ServiceProvider.GetRequiredService<IntegrationHubDbContext>()
                .Database.CanConnectAsync(cancellationToken);
            return canConnect ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("SQLite is unavailable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQLite readiness check failed.", ex);
        }
    }
}
