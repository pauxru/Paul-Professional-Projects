using System.Net;
using System.Net.Http.Json;
using AuditPlatform.Application.Abstractions;
using AuditPlatform.Application.Events;
using AuditPlatform.Application.Exports;
using AuditPlatform.Application.Query;
using AuditPlatform.Application.Retention;
using AuditPlatform.Application.Reports;
using AuditPlatform.Application.Security;
using AuditPlatform.Application.Verification;
using AuditPlatform.Domain.Events;
using AuditPlatform.Domain.Retention;
using AuditPlatform.Domain.Time;
using AuditPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AuditPlatform.IntegrationTests;

public class RetentionAndLegalHoldTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public RetentionAndLegalHoldTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Retention_PrunesOldEvents_AndChainStillVerifies()
    {
        var admin = _factory.CreateAuthClient(tenantId: "ret-clean");
        await TestData.RegisterUserLoginSchema(admin);
        var farPastSeed = _factory.Clock.UtcNow;
        _factory.Clock.Set(farPastSeed.AddDays(-400));
        for (var i = 0; i < 3; i++)
            await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: $"rp-{i}", eventTime: _factory.Clock.UtcNow));
        _factory.Clock.Set(farPastSeed);
        // A newer event that survives.
        await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: "rp-new", eventTime: _factory.Clock.UtcNow));

        // Add a wildcard 365-day policy.
        var policyResp = await admin.PostAsJsonAsync("/api/v1/retention", new { CategoryPattern = "*", RetainForDays = 365 });
        policyResp.EnsureSuccessStatusCode();

        var runResp = await admin.PostAsync("/api/v1/retention/run", null);
        runResp.EnsureSuccessStatusCode();
        var report = await runResp.Content.ReadFromJsonAsync<PruneReport>();
        Assert.True(report!.Pruned >= 3);

        var verifyResp = await admin.PostAsJsonAsync("/api/v1/verify", new { FromSequence = 1L, ToSequence = 1000L });
        verifyResp.EnsureSuccessStatusCode();
        var chain = await verifyResp.Content.ReadFromJsonAsync<ChainVerificationReport>();
        Assert.True(chain!.IsValid, chain.Reason);
    }

    [Fact]
    public async Task Retention_LegalHold_BlocksPruning()
    {
        var admin = _factory.CreateAuthClient(tenantId: "ret-hold");
        await TestData.RegisterUserLoginSchema(admin);
        var seed = _factory.Clock.UtcNow;
        _factory.Clock.Set(seed.AddDays(-500));
        var protectedResourceId = "acct-hold-1";
        // Emit two events targeting acct-hold-1 in the far past.
        for (var i = 0; i < 2; i++)
        {
            var body = new
            {
                EventType = "user.login",
                SchemaVersion = 1,
                EventTime = _factory.Clock.UtcNow,
                Actor = new { Type = 0, Id = "u-1", DisplayName = "u", Roles = new[] { "r" } },
                ActionVerb = "login",
                Category = 1,
                Resource = new { Type = "account", Id = protectedResourceId, Name = "n", ParentPath = "/" },
                Outcome = 0,
                Severity = 0,
                Source = new { Ip = "1", UserAgent = "1", Service = "1", Region = "1" },
                CorrelationId = "corr-hold",
                ClientEventId = $"hold-{i}",
                Data = System.Text.Json.JsonDocument.Parse("{\"method\":\"password\"}").RootElement
            };
            await admin.PostAsJsonAsync("/api/v1/events", body);
        }
        _factory.Clock.Set(seed);

        await admin.PostAsJsonAsync("/api/v1/retention", new { CategoryPattern = "*", RetainForDays = 30 });
        await admin.PostAsJsonAsync("/api/v1/legal-holds", new { ResourceType = "account", ResourceId = protectedResourceId, Reason = "SAR request", TicketReference = "TCK-1" });

        var runResp = await admin.PostAsync("/api/v1/retention/run", null);
        runResp.EnsureSuccessStatusCode();
        var report = await runResp.Content.ReadFromJsonAsync<PruneReport>();
        Assert.True(report!.LegalHoldSkips >= 2);

        // Events remain non-tombstoned.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var events = db.Events.Where(e => e.TenantId == "ret-hold").ToList();
        Assert.All(events, e => Assert.False(e.IsTombstoned));
    }
}

public class QueryAndPaginationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public QueryAndPaginationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Query_KeysetPagination_IsStableUnderNewInserts()
    {
        var admin = _factory.CreateAuthClient(tenantId: "page-tenant");
        await TestData.RegisterUserLoginSchema(admin);
        for (var i = 0; i < 15; i++)
            await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: $"pg-{i}"));

        var firstResp = await admin.GetAsync("/api/v1/events?pageSize=5");
        var firstPage = await firstResp.Content.ReadFromJsonAsync<EventPage>();
        Assert.NotNull(firstPage);
        Assert.Equal(5, firstPage!.Items.Count);
        Assert.False(string.IsNullOrEmpty(firstPage.NextCursor));

        // Insert MORE events between paginated calls — the cursor should still resume without drift.
        for (var i = 15; i < 20; i++)
            await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: $"pg-x-{i}"));

        var secondResp = await admin.GetAsync($"/api/v1/events?pageSize=5&cursor={firstPage.NextCursor}");
        var secondPage = await secondResp.Content.ReadFromJsonAsync<EventPage>();
        Assert.Equal(5, secondPage!.Items.Count);
        // No overlap with first page.
        var firstIds = firstPage.Items.Select(i => i.Id).ToHashSet();
        Assert.All(secondPage.Items, item => Assert.DoesNotContain(item.Id, firstIds));
    }

    [Fact]
    public async Task Query_FreeTextSearch_FindsEvents()
    {
        var admin = _factory.CreateAuthClient(tenantId: "search-tenant");
        await TestData.RegisterUserLoginSchema(admin);
        await admin.PostAsJsonAsync("/api/v1/schemas", new
        {
            EventType = "note.written",
            SchemaJson = "{ \"fields\": [ {\"name\":\"note\",\"kind\":\"string\",\"required\":true} ] }",
            Description = "Note"
        });
        var body = new
        {
            EventType = "note.written",
            SchemaVersion = 1,
            EventTime = _factory.Clock.UtcNow,
            Actor = new { Type = 0, Id = "u-1", DisplayName = "auditor-alpha", Roles = new[] { "r" } },
            ActionVerb = "write",
            Category = 0,
            Resource = new { Type = "note", Id = "n-1", Name = "the-quick-brown-fox", ParentPath = "/" },
            Outcome = 0,
            Severity = 0,
            Source = new { Ip = "1", UserAgent = "1", Service = "1", Region = "1" },
            CorrelationId = "corr-search",
            ClientEventId = "search-1",
            Data = System.Text.Json.JsonDocument.Parse("{\"note\":\"the-quick-brown-fox jumps over lazy dogs\"}").RootElement
        };
        await admin.PostAsJsonAsync("/api/v1/events", body);

        var searchResp = await admin.GetAsync("/api/v1/events?search=fox");
        var page = await searchResp.Content.ReadFromJsonAsync<EventPage>();
        Assert.NotNull(page);
        Assert.Single(page!.Items);
    }
}

public class ExportsAndReportsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ExportsAndReportsTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Export_Ndjson_ReturnsEventsAsLines()
    {
        var admin = _factory.CreateAuthClient(tenantId: "exp-nd");
        await TestData.RegisterUserLoginSchema(admin);
        for (var i = 0; i < 3; i++)
            await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: $"ex-{i}"));

        var resp = await admin.GetAsync("/api/v1/exports/ndjson");
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        foreach (var l in lines) Assert.Contains("chainHash", l, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_EvidencePack_VerifiesRoundTrip()
    {
        var admin = _factory.CreateAuthClient(tenantId: "exp-ev");
        await TestData.RegisterUserLoginSchema(admin);
        for (var i = 0; i < 3; i++)
            await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: $"ev-{i}"));
        await admin.PostAsync("/api/v1/checkpoints", null);

        var request = new { From = DateTimeOffset.MinValue.UtcDateTime, To = DateTimeOffset.MaxValue.UtcDateTime };
        var packResp = await admin.PostAsJsonAsync("/api/v1/exports/evidence", request);
        packResp.EnsureSuccessStatusCode();
        var pack = await packResp.Content.ReadFromJsonAsync<EvidencePack>();
        Assert.NotNull(pack);

        var verifyResp = await admin.PostAsJsonAsync("/api/v1/exports/evidence/verify", pack);
        var body = await verifyResp.Content.ReadAsStringAsync();
        Assert.Contains("\"valid\":true", body);
    }

    [Fact]
    public async Task PrivilegedAccessReport_FlagsOutOfHoursAndEscalations()
    {
        var admin = _factory.CreateAuthClient(tenantId: "priv-tenant");
        await TestData.RegisterUserLoginSchema(admin);
        await admin.PostAsJsonAsync("/api/v1/schemas", new
        {
            EventType = "admin.role.grant",
            SchemaJson = "{ \"fields\": [ {\"name\":\"grantedRole\",\"kind\":\"string\",\"required\":true} ] }",
            Description = "Grant role"
        });

        // Out-of-hours privileged event at 03:00 UTC.
        _factory.Clock.Set(new DateTimeOffset(2026, 3, 15, 3, 0, 0, TimeSpan.Zero));
        var escalation = new
        {
            EventType = "admin.role.grant",
            SchemaVersion = 1,
            EventTime = _factory.Clock.UtcNow,
            Actor = new { Type = 0, Id = "u-admin", DisplayName = "Owen (fictional)", Roles = new[] { "admin" } },
            ActionVerb = "grant",
            Category = (int)EventCategory.PrivilegedAccess,
            Resource = new { Type = "role", Id = "role-admin", Name = "audit-admin", ParentPath = "/roles" },
            Outcome = 0,
            Severity = (int)EventSeverity.Warning,
            Source = new { Ip = "1", UserAgent = "1", Service = "1", Region = "1" },
            CorrelationId = "corr-priv-1",
            ClientEventId = "priv-1",
            Data = System.Text.Json.JsonDocument.Parse("{\"grantedRole\":\"audit-admin\"}").RootElement
        };
        await admin.PostAsJsonAsync("/api/v1/events", escalation);

        var reportResp = await admin.GetAsync("/api/v1/reports/privileged-access?from=2026-01-01&to=2026-12-31");
        reportResp.EnsureSuccessStatusCode();
        var report = await reportResp.Content.ReadFromJsonAsync<PrivilegedAccessReport>();
        Assert.NotNull(report);
        Assert.Contains(report!.Anomalies, a => a.Type == "out-of-hours");
        Assert.Contains(report.Anomalies, a => a.Type == "permission-escalation");
    }
}
