using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lab.IntegrationTests.Infrastructure;

namespace Lab.IntegrationTests;

public sealed class NorthstarApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task HealthLive_Anonymous_ReturnsOkAndCorrelationHeader()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-Id", "test-correlation-06");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("test-correlation-06", response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task HealthReady_Anonymous_UsesSqliteReadinessCheck()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ListOrders_WithoutToken_Returns401()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/orders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListOrders_WithWrongScope_Returns403()
    {
        using var client = await AuthorizedClientAsync("orders.write");

        using var response = await client.GetAsync("/api/v1/orders");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListOrders_WithReadScope_ReturnsSeededPage()
    {
        using var client = await AuthorizedClientAsync("orders.read");

        using var response = await client.GetAsync("/api/v1/orders?page=1&pageSize=2");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, document.RootElement.GetProperty("pageSize").GetInt32());
        Assert.True(document.RootElement.GetProperty("totalCount").GetInt32() >= 3);
        Assert.Equal(2, document.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetOrder_WithReadScope_ReturnsDetails()
    {
        using var client = await AuthorizedClientAsync("orders.read");
        using var listResponse = await client.GetAsync("/api/v1/orders");
        using var listDocument = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var id = listDocument.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();

        using var response = await client.GetAsync($"/api/v1/orders/{id}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(id, document.RootElement.GetProperty("id").GetGuid());
        Assert.True(document.RootElement.TryGetProperty("customerEmail", out _));
    }

    [Fact]
    public async Task CreateOrder_WithValidRequest_Returns201AndLocation()
    {
        using var client = await AuthorizedClientAsync("orders.read orders.write");
        var request = new
        {
            customerName = "Contoso Retail (fictional)",
            customerEmail = "routing@contoso.example",
            reference = "NS-30001",
            destination = "Eldoret distribution centre"
        };

        using var response = await client.PostAsJsonAsync("/api/v1/orders", request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("NS-30001", document.RootElement.GetProperty("reference").GetString());
    }

    [Fact]
    public async Task CreateOrder_WithInvalidRequest_ReturnsProblemDetailsErrors()
    {
        using var client = await AuthorizedClientAsync("orders.write");
        var request = new
        {
            customerName = "",
            customerEmail = "not-an-email",
            reference = "X",
            destination = ""
        };

        using var response = await client.PostAsJsonAsync("/api/v1/orders", request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("errors", out var errors));
        Assert.True(errors.EnumerateObject().Any());
    }

    [Fact]
    public async Task GetOrder_UnknownId_Returns404ProblemDetails()
    {
        using var client = await AuthorizedClientAsync("orders.read");

        using var response = await client.GetAsync($"/api/v1/orders/{Guid.NewGuid()}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, document.RootElement.GetProperty("status").GetInt32());
    }

    private async Task<HttpClient> AuthorizedClientAsync(string scopes)
    {
        using var tokenClient = factory.CreateClient();
        using var tokenResponse = await tokenClient.PostAsJsonAsync(
            "/api/v1/auth/token",
            new { subject = "integration-test", scope = scopes });
        tokenResponse.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        var token = document.RootElement.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
