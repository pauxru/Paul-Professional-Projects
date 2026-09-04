using System.Net;
using System.Net.Http.Json;
using AuditPlatform.Application.Abstractions;
using AuditPlatform.Application.Events;
using AuditPlatform.Application.Query;
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

public class InterceptorAndCheckpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public InterceptorAndCheckpointTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Interceptor_BlocksUpdateOfAuditEventThroughEfSaveChanges()
    {
        var admin = _factory.CreateAuthClient(tenantId: "interceptor-t1");
        await TestData.RegisterUserLoginSchema(admin);
        await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: "int-1"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evt = db.Events.First(e => e.TenantId == "interceptor-t1");

        // Force a mutation by directly poking the private setter with reflection so we can prove
        // the interceptor rejects it before it hits the DB.
        var chainHashProp = typeof(AuditEvent).GetProperty(nameof(AuditEvent.ChainHash));
        chainHashProp!.SetValue(evt, "0000000000000000000000000000000000000000000000000000000000000000");
        db.Entry(evt).State = EntityState.Modified;
        var ex = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        Assert.Equal(DomainErrorCode.AppendOnlyViolation, ex.Code);
    }

    [Fact]
    public async Task Interceptor_BlocksDeleteOfAuditEventThroughEfSaveChanges()
    {
        var admin = _factory.CreateAuthClient(tenantId: "interceptor-t2");
        await TestData.RegisterUserLoginSchema(admin);
        await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: "int-2"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evt = db.Events.First(e => e.TenantId == "interceptor-t2");
        db.Events.Remove(evt);
        var ex = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        Assert.Equal(DomainErrorCode.AppendOnlyViolation, ex.Code);
    }

    [Fact]
    public async Task Checkpoint_ThenInclusionProof_VerifiesAllEvents()
    {
        var admin = _factory.CreateAuthClient(tenantId: "cp-tenant");
        await TestData.RegisterUserLoginSchema(admin);
        for (var i = 0; i < 8; i++)
            await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: $"cp-{i}"));

        var cpResp = await admin.PostAsync("/api/v1/checkpoints", null);
        cpResp.EnsureSuccessStatusCode();

        // Grab one event by listing.
        var listResp = await admin.GetAsync("/api/v1/events?pageSize=100");
        var page = await listResp.Content.ReadFromJsonAsync<EventPage>();
        Assert.NotNull(page);
        Assert.Equal(8, page!.Items.Count);
        var proofResp = await admin.GetAsync($"/api/v1/events/{page.Items[3].Id}/proof");
        proofResp.EnsureSuccessStatusCode();
        var proof = await proofResp.Content.ReadFromJsonAsync<InclusionProof>();
        Assert.NotNull(proof);

        var verifyResp = await admin.PostAsJsonAsync("/api/v1/verify/inclusion", proof);
        var body = await verifyResp.Content.ReadAsStringAsync();
        Assert.Contains("\"valid\":true", body);
    }

    [Fact]
    public async Task Checkpoint_InclusionProof_WithForgedLeaf_IsInvalid()
    {
        var admin = _factory.CreateAuthClient(tenantId: "cp-forge");
        await TestData.RegisterUserLoginSchema(admin);
        for (var i = 0; i < 5; i++)
            await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: $"cpf-{i}"));

        await admin.PostAsync("/api/v1/checkpoints", null);
        var listResp = await admin.GetAsync("/api/v1/events?pageSize=100");
        var page = await listResp.Content.ReadFromJsonAsync<EventPage>();
        var proofResp = await admin.GetAsync($"/api/v1/events/{page!.Items[2].Id}/proof");
        var proof = await proofResp.Content.ReadFromJsonAsync<InclusionProof>();
        var forged = proof! with { LeafHash = "0000000000000000000000000000000000000000000000000000000000000000" };

        var verifyResp = await admin.PostAsJsonAsync("/api/v1/verify/inclusion", forged);
        var body = await verifyResp.Content.ReadAsStringAsync();
        Assert.Contains("\"valid\":false", body);
    }

    [Fact]
    public async Task Checkpoint_HasSignatureThatVerifies()
    {
        var admin = _factory.CreateAuthClient(tenantId: "cp-sig");
        await TestData.RegisterUserLoginSchema(admin);
        for (var i = 0; i < 3; i++)
            await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: $"cps-{i}"));

        await admin.PostAsync("/api/v1/checkpoints", null);
        var response = await admin.GetAsync("/api/v1/checkpoints");
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<List<Checkpoint>>();
        var last = list!.Last();
        Assert.False(string.IsNullOrEmpty(last.SignatureBase64));
        Assert.False(string.IsNullOrEmpty(last.SigningKeyId));

        using var scope = _factory.Services.CreateScope();
        var verifier = scope.ServiceProvider.GetRequiredService<VerificationService>();
        Assert.True(verifier.VerifyCheckpointSignature(last));
    }
}
