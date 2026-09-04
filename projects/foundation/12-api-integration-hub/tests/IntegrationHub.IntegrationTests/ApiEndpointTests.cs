using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntegrationHub.Domain;

namespace IntegrationHub.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class ApiEndpointTests(ApiFactory factory)
{
    [Fact]
    public async Task GetConnectors_WithoutToken_Returns401()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/connectors")).StatusCode);
    }

    [Fact]
    public async Task MappingTest_WithReadOnlyScope_Returns403()
    {
        using var client = await AuthorizedClient("hub.read");
        var response = await client.PostAsJsonAsync("/api/v1/mappings/test", new
        {
            input = new { name = "Ada" },
            mappings = new[] { new { targetPath = "$.name", expression = "$.name" } }
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MappingTest_WithInvalidRequest_ReturnsProblemDetails()
    {
        using var client = await AuthorizedClient("hub.write");
        var response = await client.PostAsJsonAsync("/api/v1/mappings/test", new { input = new { name = "Ada" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(400, json["status"]!.GetValue<int>());
        Assert.NotNull(json["errors"]);
        Assert.NotNull(json["traceId"]);
    }

    [Fact]
    public async Task MappingTest_HappyPath_ReturnsOutputAndTrace()
    {
        using var client = await AuthorizedClient("hub.write");
        var response = await client.PostAsJsonAsync("/api/v1/mappings/test", new
        {
            input = new { name = " ada " },
            mappings = new[] { new { targetPath = "$.customerName", expression = "upper(trim($.name))" } }
        });
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("ADA", json["output"]!["customerName"]!.GetValue<string>());
        Assert.True(json["trace"]![0]!["success"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ConnectorRegistry_WithReadScope_ReturnsVersionedConnectors()
    {
        using var client = await AuthorizedClient("hub.read");
        var response = await client.GetAsync("/api/v1/connectors");
        response.EnsureSuccessStatusCode();
        var items = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        Assert.Contains(items, x => x!["id"]!.GetValue<string>() == "contoso-crm");
        Assert.Contains(items, x => x!["id"]!.GetValue<string>() == "acme-erp");
        Assert.Contains(items, x => x!["id"]!.GetValue<string>() == "pesagate-payments");
        Assert.All(items, x => Assert.False(string.IsNullOrWhiteSpace(x!["version"]!.GetValue<string>())));
    }

    [Fact]
    public async Task Flow_CreateActivateRunAndReadHistory_CompletesHappyPath()
    {
        using var client = await AuthorizedClient("hub.read", "hub.write");
        var definition = new FlowDefinition(
            "Manual no-op",
            new TriggerDefinition(FlowTriggerKind.Manual),
            [
                new FlowStepDefinition("trigger", FlowStepKind.Trigger, new Dictionary<string, string>()),
                new FlowStepDefinition("respond", FlowStepKind.Respond, new Dictionary<string, string>())
            ]);
        var create = await client.PostAsJsonAsync("/api/v1/flows", new
        {
            name = definition.Name,
            format = "json",
            definition = JsonSerializer.Serialize(definition)
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var flow = JsonNode.Parse(await create.Content.ReadAsStringAsync())!;
        var flowId = flow["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/flows/{flowId}/versions/1/activate", null)).StatusCode);
        var runResponse = await client.PostAsJsonAsync($"/api/v1/flows/{flowId}/runs", new { payload = new { id = "record-1" } });
        Assert.Equal(HttpStatusCode.Accepted, runResponse.StatusCode);
        var run = JsonNode.Parse(await runResponse.Content.ReadAsStringAsync())!;
        Assert.Equal("Succeeded", run["status"]!.GetValue<string>());
        var runId = run["id"]!.GetValue<Guid>();
        var detailsResponse = await client.GetAsync($"/api/v1/runs/{runId}");
        detailsResponse.EnsureSuccessStatusCode();
        var details = JsonNode.Parse(await detailsResponse.Content.ReadAsStringAsync())!;
        Assert.Equal(2, details["steps"]!.AsArray().Count);
    }

    [Fact]
    public async Task TokenEndpoint_BadCredentials_Returns401()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            clientId = "demo-client",
            clientSecret = "wrong",
            scopes = new[] { "hub.read" }
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsCorrelationAndSecurityHeaders()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-Id", "integration-test-correlation");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.Equal("integration-test-correlation", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    private async Task<HttpClient> AuthorizedClient(params string[] scopes)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await factory.GetTokenAsync(scopes));
        return client;
    }
}
