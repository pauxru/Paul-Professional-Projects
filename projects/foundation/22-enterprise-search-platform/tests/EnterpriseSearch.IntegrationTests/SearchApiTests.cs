using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseSearch.IntegrationTests.Support;

namespace EnterpriseSearch.IntegrationTests;

public sealed class SearchApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_Anonymous_ReturnsOkAndCorrelationId()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Indices_WithoutToken_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/indices");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Indices_ReadOnlyToken_Returns403()
    {
        var client = await AuthorizedClientAsync("search.read");
        var response = await client.GetAsync("/api/v1/indices");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateIndex_InvalidRequest_ReturnsProblemDetails()
    {
        var client = await AuthorizedClientAsync("search.manage");
        var response = await client.PostAsJsonAsync("/api/v1/indices", new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Invalid request", document.RootElement.GetProperty("title").GetString());
        Assert.True(document.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task IndexRefreshAndSearch_HappyPath_ReturnsDocument()
    {
        var client = await AuthorizedClientAsync("search.manage");
        var index = "api-products-" + Guid.NewGuid().ToString("N")[..8];
        var created = await client.PostAsJsonAsync("/api/v1/indices", new { name = index, alias = index });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var indexed = await client.PostAsJsonAsync($"/api/v1/indices/{index}/documents", new { id = "laptop-1", fields = new { title = "Secure laptop", body = "portable computer" }, numericFields = new { price = 250 } });
        Assert.Equal(HttpStatusCode.Accepted, indexed.StatusCode);
        var refreshed = await client.PostAsync($"/api/v1/indices/{index}/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var search = await client.PostAsJsonAsync("/api/v1/search", new { index, query = "laptop", explain = true, highlightFields = new[] { "title" } });
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using var result = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        Assert.Equal(1, result.RootElement.GetProperty("total").GetInt64());
        Assert.Equal("laptop-1", result.RootElement.GetProperty("hits")[0].GetProperty("document").GetProperty("id").GetString());
        Assert.True(result.RootElement.GetProperty("hits")[0].TryGetProperty("explanation", out var explanation));
        Assert.NotEmpty(explanation.GetProperty("terms").EnumerateArray());
    }

    [Fact]
    public async Task Analyze_WithJwt_ReturnsTokenOffsets()
    {
        var client = await AuthorizedClientAsync("search.read");
        var response = await client.PostAsJsonAsync("/api/v1/analyze", new { text = "Café laptop", analyzer = "standard" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("cafe", document.RootElement.GetProperty("tokens")[0].GetProperty("term").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("tokens")[0].GetProperty("startOffset").GetInt32());
    }

    [Fact]
    public async Task Search_MalformedQuery_ReturnsProblemDetails()
    {
        var client = await AuthorizedClientAsync("search.read");
        var response = await client.PostAsJsonAsync("/api/v1/search", new { index = "missing", query = "title:(laptop OR" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Invalid request", document.RootElement.GetProperty("title").GetString());
    }

    private async Task<HttpClient> AuthorizedClientAsync(params string[] scopes)
    {
        var client = factory.CreateClient();
        var tokenResponse = await client.PostAsJsonAsync("/api/v1/auth/token", new { subject = "test-user", scopes });
        tokenResponse.EnsureSuccessStatusCode();
        using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.RootElement.GetProperty("accessToken").GetString());
        return client;
    }
}
