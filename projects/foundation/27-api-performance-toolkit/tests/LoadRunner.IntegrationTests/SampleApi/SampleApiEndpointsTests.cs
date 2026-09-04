using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace LoadRunner.IntegrationTests.SampleApi;

public class SampleApiEndpointsTests : IClassFixture<SampleApiFactory>
{
    private readonly SampleApiFactory _factory;

    public SampleApiEndpointsTests(SampleApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Root_Returns_Ok()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_ReportsCanRead()
    {
        var client = _factory.CreateClient();
        var response = await client.GetFromJsonAsync<Dictionary<string, object>>("/health/ready");
        Assert.NotNull(response);
        Assert.True(response!.ContainsKey("status"));
    }

    [Fact]
    public async Task Catalog_ReturnsPagedProducts()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/catalog/products?page=1&pageSize=10");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("items", body);
        Assert.Contains("totalCount", body);
    }

    [Fact]
    public async Task Order_HappyPath_CreatesAndFetches()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/api/v1/orders", new
        {
            customerRef = "Acme Manufacturing",
            lines = new[]
            {
                new { productId = 1, quantity = 2 },
                new { productId = 2, quantity = 1 }
            }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var body = await create.Content.ReadAsStringAsync();
        Assert.Contains("orderId", body);
    }

    [Fact]
    public async Task Order_RejectsEmptyLines()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/api/v1/orders", new
        {
            customerRef = "Acme Manufacturing",
            lines = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }
}
