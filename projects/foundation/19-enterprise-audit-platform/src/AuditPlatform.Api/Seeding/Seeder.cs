using AuditPlatform.Api.Options;
using AuditPlatform.Application.Abstractions;
using AuditPlatform.Application.Events;
using AuditPlatform.Domain.Events;
using AuditPlatform.Domain.Retention;
using AuditPlatform.Domain.Time;
using Microsoft.Extensions.Options;

namespace AuditPlatform.Api.Seeding;

/// <summary>
/// Idempotent seed data for the fictional tenant "example-bank". Registers a handful of core
/// schemas, applies a wildcard retention policy, and creates a small demo chain so the API is
/// interactive from a cold boot in Development.
/// </summary>
public static class Seeder
{
    public static async Task SeedAsync(IServiceProvider services, IHostEnvironment env, CancellationToken ct)
    {
        var opts = services.GetRequiredService<IOptions<SeedOptions>>().Value;
        if (!opts.Enabled) return;
        if (!env.IsDevelopment()) return;

        var schemas = services.GetRequiredService<ISchemaRegistry>();
        await EnsureSchemaAsync(schemas, "user.login", "{ \"fields\": [ {\"name\":\"method\",\"kind\":\"string\",\"required\":true} ] }", "User signed in", ct);
        await EnsureSchemaAsync(schemas, "user.logout", "{ \"fields\": [ {\"name\":\"reason\",\"kind\":\"string\",\"required\":false} ] }", "User signed out", ct);
        await EnsureSchemaAsync(schemas, "account.update", "{ \"fields\": [ {\"name\":\"accountNumber\",\"kind\":\"string\",\"required\":true}, {\"name\":\"changedFields\",\"kind\":\"array\",\"required\":true} ] }", "Account details updated", ct);
        await EnsureSchemaAsync(schemas, "admin.role.grant", "{ \"fields\": [ {\"name\":\"grantedRole\",\"kind\":\"string\",\"required\":true} ] }", "Admin granted role", ct);
        await EnsureSchemaAsync(schemas, "audit.log.read", "{ \"fields\": [ {\"name\":\"itemsReturned\",\"kind\":\"integer\",\"required\":false} ] }", "Audit log read (meta-audit)", ct);
        await EnsureSchemaAsync(schemas, "audit.retention.pruned", "{ \"fields\": [ {\"name\":\"considered\",\"kind\":\"integer\",\"required\":true}, {\"name\":\"pruned\",\"kind\":\"integer\",\"required\":true}, {\"name\":\"legalHoldSkips\",\"kind\":\"integer\",\"required\":true} ] }", "Retention pruner run", ct);
        await EnsureSchemaAsync(schemas, "export.evidence", "{ \"fields\": [ {\"name\":\"packId\",\"kind\":\"string\",\"required\":true} ] }", "Evidence pack export", ct);

        var retention = services.GetRequiredService<IRetentionStore>();
        var clock = services.GetRequiredService<IClock>();
        var existing = await retention.ListAsync(opts.DefaultTenantId, ct);
        if (existing.All(p => p.CategoryPattern != "*"))
        {
            await retention.AddAsync(RetentionPolicy.Create(Guid.NewGuid(), opts.DefaultTenantId, "*", 365, clock.UtcNow), ct);
            await retention.SaveChangesAsync(ct);
        }
        if (existing.All(p => p.CategoryPattern != nameof(EventCategory.PrivilegedAccess)))
        {
            await retention.AddAsync(RetentionPolicy.Create(Guid.NewGuid(), opts.DefaultTenantId, nameof(EventCategory.PrivilegedAccess), 2555, clock.UtcNow), ct);
            await retention.SaveChangesAsync(ct);
        }

        var events = services.GetRequiredService<IAuditEventStore>();
        var latest = await events.GetLatestForTenantAsync(opts.DefaultTenantId, ct);
        if (latest is not null) return;

        var ingest = services.GetRequiredService<AuditIngestService>();
        // Seed six demo events for the fictional tenant "example-bank".
        var actor = new ActorInput(ActorType.User, "u-1001", "Alice Njeri (fictional)", new List<string> { "banker" });
        var admin = new ActorInput(ActorType.User, "u-9001", "Owen Kariuki (fictional)", new List<string> { "admin", "audit:admin" });
        var source = new SourceInput("203.0.113.10", "seed-client/1.0", "core-banking", "eu-west-1");

        await ingest.IngestAsync(new IngestEventRequest(
            opts.DefaultTenantId, "user.login", 1, clock.UtcNow.AddMinutes(-30), actor, "login", EventCategory.Authentication,
            new ResourceInput("session", Guid.NewGuid().ToString("n"), "sign-in", "/auth"),
            EventOutcome.Success, EventSeverity.Info, source,
            Guid.NewGuid().ToString("n"), null, null, "seed-1",
            System.Text.Json.JsonDocument.Parse("{\"method\":\"password\"}").RootElement, null, null), ct);

        await ingest.IngestAsync(new IngestEventRequest(
            opts.DefaultTenantId, "account.update", 1, clock.UtcNow.AddMinutes(-25), actor, "update", EventCategory.DataChange,
            new ResourceInput("account", "acct-4001", "Ada Wanjiru (fictional)", "/banking/accounts"),
            EventOutcome.Success, EventSeverity.Notice, source,
            Guid.NewGuid().ToString("n"), null, null, "seed-2",
            System.Text.Json.JsonDocument.Parse("{\"accountNumber\":\"4001\",\"changedFields\":[\"address\"]}").RootElement,
            System.Text.Json.JsonDocument.Parse("{\"address\":\"Old\"}").RootElement,
            System.Text.Json.JsonDocument.Parse("{\"address\":\"New\"}").RootElement), ct);

        await ingest.IngestAsync(new IngestEventRequest(
            opts.DefaultTenantId, "admin.role.grant", 1, clock.UtcNow.AddMinutes(-20), admin, "grant", EventCategory.PrivilegedAccess,
            new ResourceInput("role", "role-admin", "audit-admin", "/security/roles"),
            EventOutcome.Success, EventSeverity.Warning, source,
            Guid.NewGuid().ToString("n"), null, null, "seed-3",
            System.Text.Json.JsonDocument.Parse("{\"grantedRole\":\"audit-admin\"}").RootElement, null, null), ct);
    }

    private static async Task EnsureSchemaAsync(ISchemaRegistry registry, string type, string schema, string desc, CancellationToken ct)
    {
        var latest = await registry.GetLatestAsync(type, ct);
        if (latest is not null) return;
        await registry.RegisterAsync(type, schema, desc, ct);
    }
}
