using System.Net;
using System.Net.Http.Json;
using AuditPlatform.Application.Abstractions;
using AuditPlatform.Application.Events;
using AuditPlatform.Application.Verification;
using AuditPlatform.Domain.Errors;
using AuditPlatform.Domain.Events;
using AuditPlatform.Domain.Integrity;
using AuditPlatform.Infrastructure.Persistence;
using AuditPlatform.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AuditPlatform.IntegrationTests;

public class IngestTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IngestTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Ingest_SingleEvent_ProducesChainedEvent()
    {
        var admin = _factory.CreateAuthClient();
        await TestData.RegisterUserLoginSchema(admin);
        var response = await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: "ing-1"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResult>();
        Assert.NotNull(body);
        Assert.True(body!.Accepted);
        Assert.Equal(1, body.SequenceNumber);
        Assert.False(string.IsNullOrEmpty(body.ChainHash));
    }

    [Fact]
    public async Task Ingest_WithDuplicateClientEventId_IsIdempotent()
    {
        var admin = _factory.CreateAuthClient();
        await TestData.RegisterUserLoginSchema(admin);
        var first = await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: "idem-1"));
        first.EnsureSuccessStatusCode();
        var second = await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: "idem-1"));
        second.EnsureSuccessStatusCode();
        var body = await second.Content.ReadFromJsonAsync<IngestResult>();
        Assert.True(body!.WasDuplicate);
    }

    [Fact]
    public async Task Ingest_WithoutRegisteredSchema_ReturnsUnprocessable()
    {
        var admin = _factory.CreateAuthClient();
        var response = await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(eventType: "unknown.type", clientEventId: "unk-1"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_BatchWithMixedResults_ReportsPerEvent()
    {
        var admin = _factory.CreateAuthClient();
        await TestData.RegisterUserLoginSchema(admin);

        var body = new
        {
            Events = new object[]
            {
                TestData.SampleIngestBody(clientEventId: "b-1"),
                TestData.SampleIngestBody(eventType: "nope", clientEventId: "b-2"),
                TestData.SampleIngestBody(clientEventId: "b-3")
            }
        };
        var response = await admin.PostAsJsonAsync("/api/v1/events/batch", body);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"accepted\":2", json);
        Assert.Contains("\"rejected\":1", json);
    }
}

public class ChainVerificationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ChainVerificationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Verify_CleanChain_Passes()
    {
        var admin = _factory.CreateAuthClient();
        await TestData.RegisterUserLoginSchema(admin);
        for (var i = 0; i < 5; i++)
            await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: $"clean-{i}"));

        var verifyResp = await admin.PostAsJsonAsync("/api/v1/verify", new { FromSequence = 1L, ToSequence = 100L });
        verifyResp.EnsureSuccessStatusCode();
        var report = await verifyResp.Content.ReadFromJsonAsync<ChainVerificationReport>();
        Assert.True(report!.IsValid, report.Reason);
    }

    [Fact]
    public async Task Verify_TamperedPayload_DetectsAtCorrectSequence()
    {
        var admin = _factory.CreateAuthClient(tenantId: "tamper-payload");
        await TestData.RegisterUserLoginSchema(admin);
        for (var i = 0; i < 3; i++)
            await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: $"tp-{i}"));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Directly mutate the DB *bypassing* the DbContext interceptor via raw SQL — the
            // point is to prove verification catches out-of-band tampering. Parameterise the
            // JSON payload so the `{` characters aren't treated as format specifiers.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE AuditEvents SET PayloadJson = {0} WHERE TenantId = {1} AND SequenceNumber = 2",
                "{\"tampered\":true}", "tamper-payload");
        }

        var verifyResp = await admin.PostAsJsonAsync("/api/v1/verify", new { FromSequence = 1L, ToSequence = 100L });
        var report = await verifyResp.Content.ReadFromJsonAsync<ChainVerificationReport>();
        Assert.False(report!.IsValid);
        Assert.Equal(2L, report.BrokenAtSequence);
    }

    [Fact]
    public async Task Verify_DeletedEvent_DetectsChainBreak()
    {
        var admin = _factory.CreateAuthClient(tenantId: "tamper-delete");
        await TestData.RegisterUserLoginSchema(admin);
        for (var i = 0; i < 4; i++)
            await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: $"td-{i}"));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM AuditEvents WHERE TenantId = 'tamper-delete' AND SequenceNumber = 2");
        }

        var verifyResp = await admin.PostAsJsonAsync("/api/v1/verify", new { FromSequence = 1L, ToSequence = 100L });
        var report = await verifyResp.Content.ReadFromJsonAsync<ChainVerificationReport>();
        Assert.False(report!.IsValid);
    }

    [Fact]
    public async Task Verify_ReorderedEvent_Detected()
    {
        var admin = _factory.CreateAuthClient(tenantId: "tamper-reorder");
        await TestData.RegisterUserLoginSchema(admin);
        for (var i = 0; i < 3; i++)
            await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: $"tr-{i}"));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Swap the sequence numbers of events 1 and 2 to simulate reordering — this breaks
            // the "PreviousChainHash equals prior event's ChainHash" invariant.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE AuditEvents SET SequenceNumber = -1 WHERE TenantId = 'tamper-reorder' AND SequenceNumber = 1");
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE AuditEvents SET SequenceNumber = 1 WHERE TenantId = 'tamper-reorder' AND SequenceNumber = 2");
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE AuditEvents SET SequenceNumber = 2 WHERE TenantId = 'tamper-reorder' AND SequenceNumber = -1");
        }

        var verifyResp = await admin.PostAsJsonAsync("/api/v1/verify", new { FromSequence = 1L, ToSequence = 100L });
        var report = await verifyResp.Content.ReadFromJsonAsync<ChainVerificationReport>();
        Assert.False(report!.IsValid);
    }
}
