using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldOps.Api;
using FieldOps.Application;
using FieldOps.Domain;
using FieldOps.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldOps.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class CrossTenantIsolationTests(ApiFactory factory)
{
    [Fact]
    public async Task ReadForeignTenantJobById_Returns404()
    {
        var foreignId = await GetFirstJobId("jua-kali-manufacturing");
        var savanna = await factory.CreateTenantClientAsync();
        var response = await savanna.GetAsync($"/api/v1/jobs/{foreignId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateForeignTenantJob_IsRejectedAs404()
    {
        var foreignId = await GetFirstJobId("jua-kali-manufacturing");
        var savanna = await factory.CreateTenantClientAsync();
        var response = await savanna.PutAsJsonAsync(
            $"/api/v1/jobs/{foreignId}/status",
            new { status = "Scheduled" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListJobs_NeverLeaksForeignTenantRows()
    {
        var savanna = await factory.CreateTenantClientAsync();
        var response = await savanna.GetFromJsonAsync<JsonElement>("/api/v1/jobs?page=1&pageSize=100");
        var tenantIds = response.GetProperty("items")
            .EnumerateArray()
            .Select(x => x.GetProperty("tenantId").GetGuid())
            .Distinct()
            .ToArray();
        Assert.Equal([DemoData.SavannaId], tenantIds);
    }

    [Fact]
    public async Task ForgottenFilterRepositoryPath_IsCaughtBySaveInterceptor()
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>()
            .Set(DemoData.SavannaId, "savanna-logistics");
        var repository = scope.ServiceProvider.GetRequiredService<UnsafeJobRepository>();
        var db = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        var foreign = await db.Jobs.IgnoreQueryFilters()
            .FirstAsync(x => x.TenantId == DemoData.JuaKaliId);
        if (foreign.Status == JobStatus.Draft)
        {
            foreign.TransitionTo(JobStatus.Scheduled, factory.Clock.UtcNow);
        }
        else
        {
            db.Entry(foreign).State = EntityState.Modified;
        }

        await Assert.ThrowsAsync<CrossTenantAccessException>(() => repository.SaveAsync(default));
    }

    [Fact]
    public async Task IgnoreQueryFiltersAdminPath_RegularOwnerGets403()
    {
        var owner = await factory.CreateTenantClientAsync();
        var response = await owner.GetAsync("/api/v1/admin/jobs");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task IgnoreQueryFiltersAdminPath_PlatformAdminSeesAllTenants()
    {
        var admin = await factory.CreateTenantClientAsync("platform@fieldops.demo");
        var response = await admin.GetAsync("/api/v1/admin/jobs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var jobs = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tenants = jobs.EnumerateArray().Select(x => x.GetProperty("tenantId").GetGuid()).Distinct().ToArray();
        Assert.Contains(DemoData.SavannaId, tenants);
        Assert.Contains(DemoData.JuaKaliId, tenants);
        Assert.Contains(DemoData.AcmeId, tenants);
    }

    private async Task<Guid> GetFirstJobId(string tenant)
    {
        var client = await factory.CreateTenantClientAsync(tenant: tenant);
        var response = await client.GetFromJsonAsync<JsonElement>("/api/v1/jobs?page=1&pageSize=10");
        return response.GetProperty("items")[0].GetProperty("id").GetGuid();
    }
}
