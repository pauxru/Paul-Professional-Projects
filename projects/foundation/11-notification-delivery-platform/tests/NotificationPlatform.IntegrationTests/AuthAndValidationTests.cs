namespace NotificationPlatform.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NotificationPlatform.Api.Auth;
using NotificationPlatform.Application.Notifications;
using NotificationPlatform.Domain.Common;
using NotificationPlatform.Infrastructure.Seed;
using Xunit;

public sealed class AuthAndValidationTests : IAsyncLifetime
{
    private readonly NotificationAppFactory _factory = new();
    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();
    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task HealthEndpointsRespond()
    {
        var client = _factory.CreateClient();
        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task UnauthenticatedRequestGets401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/notifications", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task WrongScopeGets403()
    {
        var client = _factory.CreateClient();
        await client.AsTenantAsync(SeedData.ContosoTenantId, Policies.ViewAnalytics);
        var payload = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", null, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, null);
        var resp = await client.PostAsJsonAsync("/api/v1/notifications", payload, DemoJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task OpenApiEndpointIsExposed()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}

internal static class DemoJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}
