using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Northstar.Secrets.IntegrationTests;

public sealed class ApiContractTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task SecretsEndpoint_WithoutJwt_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/secrets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RegisterSecret_WithoutManageScope_ReturnsForbidden()
    {
        using var client = await CreateAuthenticatedClientAsync("reader", ["secrets.read"]);

        var response = await client.PostAsJsonAsync("/api/v1/secrets", ValidSecretRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RegisterSecret_WithInvalidRequest_ReturnsProblemDetails()
    {
        using var client = await CreateAuthenticatedClientAsync("manager", ["secrets.manage"]);
        var request = ValidSecretRequest() with
        {
            Name = "",
            RotationIntervalHours = 0
        };

        var response = await client.PostAsJsonAsync("/api/v1/secrets", request);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("validation", payload.GetProperty("title").GetString()!,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(payload.TryGetProperty("errors", out var errors));
        Assert.True(errors.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task RegisterAndListSecret_WithManageScope_ReturnsCreatedResource()
    {
        var managerName = $"manager-{Guid.NewGuid():N}";
        using var client = await CreateAuthenticatedClientAsync(managerName, ["secrets.manage"]);
        await AddManagementPolicyAsync(client, managerName);
        var request = ValidSecretRequest();

        var created = await client.PostAsJsonAsync("/api/v1/secrets", request);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/secrets?pageSize=100");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains(
            list.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("name").GetString() == request.Name);
    }

    [Fact]
    public async Task ReadValue_WithoutReason_ReturnsValidationProblem()
    {
        var actor = $"reader-{Guid.NewGuid():N}";
        var managerName = $"manager-{Guid.NewGuid():N}";
        using var manager = await CreateAuthenticatedClientAsync(
            managerName, ["secrets.manage"]);
        await AddManagementPolicyAsync(manager, managerName);
        var request = ValidSecretRequest();
        await manager.PostAsJsonAsync("/api/v1/secrets", request);
        await manager.PostAsJsonAsync("/api/v1/policies", new
        {
            subject = actor,
            pathPattern = request.Name,
            canManageMetadata = false,
            canReadValues = true,
            canOperateRotations = false,
            canBreakGlass = false
        });
        using var reader = await CreateAuthenticatedClientAsync(actor, ["secrets.read"]);

        var response = await reader.GetAsync(
            $"/api/v1/secrets/{request.Name.Replace('/', '~')}/value");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task ReadValue_WithScopeAndPathPolicy_ReturnsAuditedRuntimeValue()
    {
        var actor = $"reader-{Guid.NewGuid():N}";
        var managerName = $"manager-{Guid.NewGuid():N}";
        using var manager = await CreateAuthenticatedClientAsync(
            managerName, ["secrets.manage"]);
        await AddManagementPolicyAsync(manager, managerName);
        var request = ValidSecretRequest();
        await manager.PostAsJsonAsync("/api/v1/secrets", request);
        await manager.PostAsJsonAsync("/api/v1/policies", new
        {
            subject = actor,
            pathPattern = request.Name,
            canManageMetadata = false,
            canReadValues = true,
            canOperateRotations = false,
            canBreakGlass = false
        });
        using var reader = await CreateAuthenticatedClientAsync(actor, ["secrets.read"]);

        var response = await reader.GetAsync(
            $"/api/v1/secrets/{request.Name.Replace('/', '~')}/value?reason=integration-test");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("value").GetString()));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task HealthAndOpenApi_AreAvailableWithoutExternalInfrastructure()
    {
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/health/ready");
        var openApi = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(
        string subject,
        IReadOnlyList<string> scopes)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new { subject, scopes });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", payload.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task AddManagementPolicyAsync(HttpClient client, string subject)
    {
        var response = await client.PostAsJsonAsync("/api/v1/policies", new
        {
            subject,
            pathPattern = "**",
            canManageMetadata = true,
            canReadValues = false,
            canOperateRotations = true,
            canBreakGlass = false
        });
        response.EnsureSuccessStatusCode();
    }

    private static SecretRequest ValidSecretRequest()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new SecretRequest(
            $"api-{suffix}/test/credential",
            "ApiKey",
            "Northstar Platform Engineering (fictional)",
            "test",
            "High",
            ["synthetic"],
            "Integration test credential.",
            24,
            72,
            1);
    }

    private sealed record SecretRequest(
        string Name,
        string Type,
        string OwnerTeam,
        string Environment,
        string Criticality,
        IReadOnlyList<string> Tags,
        string Description,
        int RotationIntervalHours,
        int MaxAgeHours,
        int GracePeriodHours);
}
