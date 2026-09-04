using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Contoso.Storefront.Api.Endpoints;
using Contoso.Storefront.Api.Security;
using Contoso.Storefront.Infrastructure.Adapters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Contoso.Storefront.IntegrationTests;

public sealed class ApiIntegrationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task HealthLive_WhenDependenciesHealthy_ReturnsHealthy()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", json.RootElement.GetProperty("status").GetString());
        Assert.True(json.RootElement.GetProperty("checks").TryGetProperty("process", out _));
    }

    [Fact]
    public async Task HealthReady_WhenDependenciesHealthy_ReturnsAllDependencyChecks()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var checks = json.RootElement.GetProperty("checks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(checks.TryGetProperty("database", out _));
        Assert.True(checks.TryGetProperty("cache", out _));
        Assert.True(checks.TryGetProperty("bus", out _));
        Assert.True(checks.TryGetProperty("migrations", out _));
        Assert.False(checks.TryGetProperty("process", out _));
    }

    [Fact]
    public async Task HealthStartup_AfterDependencyWait_ReturnsHealthyAndOnlyStartupCheck()
    {
        using var client = factory.CreateClient();
        HttpResponseMessage? response = null;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            response = await client.GetAsync("/health/startup");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var checks = json.RootElement.GetProperty("checks");
        Assert.True(checks.TryGetProperty("startup", out _));
        Assert.Single(checks.EnumerateObject());
    }

    [Fact]
    public async Task HealthReady_WhenCacheFaulted_IsUnhealthyWhileLiveRemainsHealthy()
    {
        var cache = factory.Services.GetRequiredService<MemoryCacheHealthProbe>();
        cache.IsHealthy = false;
        try
        {
            using var client = factory.CreateClient();
            var ready = await client.GetAsync("/health/ready");
            var live = await client.GetAsync("/health/live");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }
        finally
        {
            cache.IsHealthy = true;
        }
    }

    [Fact]
    public async Task HealthReady_WhenBusFaulted_IsUnhealthyWhileStartupRemainsDistinct()
    {
        var bus = factory.Services.GetRequiredService<InMemoryMessageBus>();
        bus.IsHealthy = false;
        try
        {
            using var client = factory.CreateClient();
            var ready = await client.GetAsync("/health/ready");
            var startup = await client.GetAsync("/health/startup");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal(HttpStatusCode.OK, startup.StatusCode);
        }
        finally
        {
            bus.IsHealthy = true;
        }
    }

    [Fact]
    public async Task HealthReady_WhenMigrationMissing_IsUnhealthyWhileLiveRemainsHealthy()
    {
        var migrations = factory.Services.GetRequiredService<TestMigrationStatus>();
        migrations.IsCurrent = false;
        try
        {
            using var client = factory.CreateClient();
            var ready = await client.GetAsync("/health/ready");
            var live = await client.GetAsync("/health/live");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }
        finally
        {
            migrations.IsCurrent = true;
        }
    }

    [Fact]
    public async Task Catalogue_WithoutToken_Returns401()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/catalogue/products");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Catalogue_WithWrongScope_Returns403()
    {
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, StorefrontScopes.OrdersWrite);

        var response = await client.GetAsync("/api/v1/catalogue/products");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Catalogue_WithReadScope_ReturnsSeededProduct()
    {
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, StorefrontScopes.CatalogueRead);

        var response = await client.GetAsync("/api/v1/catalogue/products");
        var products = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            products.EnumerateArray(),
            product => product.GetProperty("id").GetGuid() == ApiFactory.ProductId);
    }

    [Fact]
    public async Task CreateOrder_WithMissingIdempotencyHeader_ReturnsValidationProblem()
    {
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, StorefrontScopes.OrdersWrite);

        var response = await client.PostAsJsonAsync(
            "/api/v1/orders",
            new CreateOrderRequest(
                "customer-1",
                [new CreateOrderItemRequest(ApiFactory.ProductId, 1)]));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.True(problem.GetProperty("errors").TryGetProperty("idempotencyKey", out _));
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task CreateOrder_WithValidRequest_Returns201AndOrderTotal()
    {
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, StorefrontScopes.OrdersWrite);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var response = await client.PostAsJsonAsync(
            "/api/v1/orders",
            new CreateOrderRequest(
                "customer-happy-path",
                [new CreateOrderItemRequest(ApiFactory.ProductId, 2)]));
        var order = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal(37m, order.GetProperty("total").GetDecimal());
        Assert.Equal("Submitted", order.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreateOrder_WithRepeatedIdempotencyKey_ReturnsOriginalOrder()
    {
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, StorefrontScopes.OrdersWrite);
        var key = Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var body = new CreateOrderRequest(
            "customer-replay",
            [new CreateOrderItemRequest(ApiFactory.ProductId, 1)]);

        var first = await client.PostAsJsonAsync("/api/v1/orders", body);
        var firstOrder = await first.Content.ReadFromJsonAsync<JsonElement>();
        var second = await client.PostAsJsonAsync("/api/v1/orders", body);
        var replayedOrder = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            firstOrder.GetProperty("id").GetGuid(),
            replayedOrder.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task CreateOrder_WithUnknownProduct_Returns422Problem()
    {
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, StorefrontScopes.OrdersWrite);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var response = await client.PostAsJsonAsync(
            "/api/v1/orders",
            new CreateOrderRequest(
                "customer-invalid-product",
                [new CreateOrderItemRequest(Guid.NewGuid(), 1)]));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(422, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task AnyRequest_WithInboundCorrelationId_EchoesItOnResponse()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-Id", "integration-correlation-28");

        var response = await client.SendAsync(request);

        Assert.Equal(
            "integration-correlation-28",
            response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task PricePreview_WhenDarkLaunchEnabled_ComputesCandidateWithoutChangingCurrentPrice()
    {
        using var darkFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Features:NewPricingDarkLaunch"] = "true"
                    })));
        using var client = darkFactory.CreateClient();
        await AuthenticateAsync(client, StorefrontScopes.CatalogueRead);

        var preview = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/catalogue/products/{ApiFactory.ProductId}/price-preview");

        Assert.True(preview.GetProperty("darkLaunchEvaluated").GetBoolean());
        Assert.Equal(18.50m, preview.GetProperty("currentPrice").GetDecimal());
        Assert.Equal(17.58m, preview.GetProperty("candidatePrice").GetDecimal());
    }

    [Fact]
    public async Task MetricsEndpoint_ReturnsPrometheusGateMetrics()
    {
        using var client = factory.CreateClient();

        var metrics = await client.GetStringAsync("/metrics");

        Assert.Contains("storefront_requests_total", metrics);
        Assert.Contains("storefront_error_rate", metrics);
        Assert.Contains("storefront_request_duration_ms_p95", metrics);
    }

    private static async Task AuthenticateAsync(HttpClient client, params string[] scopes)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/token",
            new TokenRequest("integration-tests", scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token!.AccessToken);
    }
}
