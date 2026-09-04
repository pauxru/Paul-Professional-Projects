using JobScheduler.Application.Abstractions;
using JobScheduler.Domain;
using JobScheduler.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace JobScheduler.Infrastructure.Seed;

/// <summary>
/// Seeds a fictional job catalogue for the Northstar Platform Team (fictional). Idempotent: it only
/// inserts a definition when one of that name does not already exist, so it is safe to run on every
/// startup and across multiple hosts.
/// </summary>
public sealed class DemoDataSeeder(
    IJobDefinitionStore definitions,
    IClock clock,
    ILogger<DemoDataSeeder> logger)
{
    private const string Owner = "Northstar Platform Team (fictional)";

    public async Task SeedAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var wanted = BuildCatalogue(now);
        int created = 0;

        foreach (var def in wanted)
        {
            if (await definitions.GetByNameAsync(def.Name, ct) is not null)
            {
                continue;
            }
            await definitions.AddAsync(def, ct);
            created++;
        }

        if (created > 0)
        {
            await definitions.SaveChangesAsync(ct);
            logger.LogInformation("Seeded {Count} demo job definition(s) for {Owner}.", created, Owner);
        }
    }

    private static IReadOnlyList<JobDefinition> BuildCatalogue(DateTimeOffset now)
    {
        return
        [
            JobDefinition.Create(
                "nightly-sales-report", "report-generator", now,
                payloadJson: "{\"rows\":500}",
                queue: "reports", priority: 5, owner: Owner,
                triggerType: TriggerType.Cron, cronExpression: "0 2 * * *", timeZoneId: "America/New_York",
                misfirePolicy: MisfirePolicy.FireNow,
                retryStrategy: RetryStrategy.ExponentialJitter, maxAttempts: 3, timeoutSeconds: 120),

            JobDefinition.Create(
                "customer-etl", "csv-transform", now,
                payloadJson: "{}",
                queue: "etl", priority: 3, owner: Owner, singleton: true,
                tags: ["etl"],
                triggerType: TriggerType.Interval, intervalSeconds: 3600,
                misfirePolicy: MisfirePolicy.SkipToNext,
                retryStrategy: RetryStrategy.Exponential, maxAttempts: 4, timeoutSeconds: 300),

            JobDefinition.Create(
                "artifact-cleanup", "cleanup", now,
                payloadJson: "{\"olderThanDays\":30}",
                queue: "maintenance", priority: 1, owner: Owner,
                triggerType: TriggerType.Cron, cronExpression: "0 3 * * *", timeZoneId: "America/New_York",
                misfirePolicy: MisfirePolicy.SkipToNext,
                retryStrategy: RetryStrategy.Fixed, maxAttempts: 2, timeoutSeconds: 120),

            JobDefinition.Create(
                "flaky-demo", "flaky", now,
                payloadJson: "{\"failTimes\":2}",
                queue: "default", priority: 2, owner: Owner,
                triggerType: TriggerType.Manual,
                retryStrategy: RetryStrategy.ExponentialJitter, retryBaseSeconds: 2, retryMaxSeconds: 30,
                maxAttempts: 5, timeoutSeconds: 30),

            JobDefinition.Create(
                "slow-demo", "slow", now,
                payloadJson: "{\"durationSeconds\":30}",
                queue: "default", priority: 2, owner: Owner,
                triggerType: TriggerType.Manual,
                retryStrategy: RetryStrategy.Fixed, retryBaseSeconds: 5, maxAttempts: 1, timeoutSeconds: 5),

            // A three-step DAG: extract -> transform -> load (fan-out/fan-in demo).
            JobDefinition.Create(
                "etl-extract", "csv-transform", now,
                payloadJson: "{}", queue: "etl", priority: 4, owner: Owner,
                triggerType: TriggerType.Manual, maxAttempts: 3, timeoutSeconds: 60),

            JobDefinition.Create(
                "etl-transform", "csv-transform", now,
                payloadJson: "{}", queue: "etl", priority: 4, owner: Owner,
                dependsOn: ["etl-extract"],
                triggerType: TriggerType.Manual, maxAttempts: 3, timeoutSeconds: 60),

            JobDefinition.Create(
                "etl-load", "report-generator", now,
                payloadJson: "{\"rows\":100}", queue: "etl", priority: 4, owner: Owner,
                dependsOn: ["etl-transform"],
                triggerType: TriggerType.Manual, maxAttempts: 3, timeoutSeconds: 60)
        ];
    }
}
