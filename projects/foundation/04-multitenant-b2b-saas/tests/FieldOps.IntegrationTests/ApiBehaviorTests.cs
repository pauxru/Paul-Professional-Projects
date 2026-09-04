using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldOps.Domain;

namespace FieldOps.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class ApiBehaviorTests(ApiFactory factory)
{
    [Fact]
    public async Task Jobs_WithoutAuthentication_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/jobs");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Jobs_WithJwtTenantOnly_ResolvesTenant()
    {
        var client = await factory.CreateTenantClientAsync(includeTenantHeader: false);
        var response = await client.GetAsync("/api/v1/jobs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Jobs_WithConflictingHeaderAndJwt_ReturnsTenantProblem()
    {
        var client = await factory.CreateTenantClientAsync(includeTenantHeader: false);
        client.DefaultRequestHeaders.Add("X-Tenant", "jua-kali-manufacturing");
        var response = await client.GetAsync("/api/v1/jobs");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://fieldops.local/problems/tenant-resolution", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task CreateJob_MissingTitle_ReturnsValidationProblem()
    {
        var client = await factory.CreateTenantClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/jobs", new
        {
            title = "",
            description = "invalid",
            priority = "Normal",
            scheduleStart = "2026-09-04T08:00:00Z",
            scheduleEnd = "2026-09-04T10:00:00Z",
            slaDueAt = "2026-09-04T12:00:00Z"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.True(problem.GetProperty("errors").TryGetProperty("title", out _));
    }

    [Fact]
    public async Task CreateAsset_AsViewer_Returns403()
    {
        var client = await factory.CreateTenantClientAsync("viewer@fieldops.demo");
        var response = await client.PostAsJsonAsync("/api/v1/assets", new
        {
            assetTag = $"VIEW-{Guid.NewGuid():N}",
            name = "Denied asset",
            category = "Test",
            location = "Nairobi"
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetJob_UnknownId_Returns404()
    {
        var client = await factory.CreateTenantClientAsync();
        var response = await client.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateAsset_AsOwner_Returns201()
    {
        var client = await factory.CreateTenantClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/assets", new
        {
            assetTag = $"EQ-{Guid.NewGuid():N}",
            name = "Portable Generator",
            category = "Power",
            location = "Nairobi Depot",
            nextMaintenanceDate = "2026-11-01"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
    }

    [Fact]
    public async Task Jobs_Pagination_CapsPageSizeAt100()
    {
        var client = await factory.CreateTenantClientAsync();
        var response = await client.GetFromJsonAsync<JsonElement>("/api/v1/jobs?page=1&pageSize=1000");
        Assert.Equal(100, response.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, response.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task CorrelationId_InboundValue_IsEchoed()
    {
        var client = await factory.CreateTenantClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/usage");
        request.Headers.Add("X-Correlation-Id", "integration-correlation-04");
        var response = await client.SendAsync(request);
        Assert.Equal("integration-correlation-04", response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task HealthReady_WithoutAuthentication_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ActiveOrganizationSwitch_IssuesUsableTenantToken()
    {
        var client = await factory.CreateTenantClientAsync();
        var switched = await client.PostAsJsonAsync("/api/v1/auth/switch", new { tenantSlug = "jua-kali-manufacturing" });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        var payload = await switched.Content.ReadFromJsonAsync<JsonElement>();
        var second = factory.CreateClient();
        second.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", payload.GetProperty("accessToken").GetString());
        second.DefaultRequestHeaders.Add("X-Tenant", "jua-kali-manufacturing");
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v1/jobs")).StatusCode);
    }

    [Fact]
    public async Task Invitation_CreateAndAccept_CompletesFlow()
    {
        var owner = await factory.CreateTenantClientAsync();
        var created = await owner.PostAsJsonAsync("/api/v1/invitations", new
        {
            email = "new-user@example.test",
            role = "Technician",
            lifetimeHours = 24
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var payload = await created.Content.ReadFromJsonAsync<JsonElement>();

        var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Tenant", "savanna-logistics");
        var accepted = await anonymous.PostAsJsonAsync("/api/v1/invitations/accept", new
        {
            token = payload.GetProperty("token").GetString(),
            userId = Guid.NewGuid()
        });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }
}
