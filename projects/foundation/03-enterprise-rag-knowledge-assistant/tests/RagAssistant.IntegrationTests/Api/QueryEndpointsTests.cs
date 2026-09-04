using System.Net;
using System.Net.Http.Json;
using RagAssistant.Api.Contracts;
using RagAssistant.Domain.Documents;
using RagAssistant.IntegrationTests.Fixtures;

namespace RagAssistant.IntegrationTests.Api;

public sealed class QueryEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public QueryEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Query_AsEmployee_ReturnsAnswerWithCitations()
    {
        var client = await _factory.AuthenticatedClientAsync(
            "alice", ["employee"], ["hr"], Classification.Internal);

        var response = await client.PostAsJsonAsync("/api/v1/query", new QueryRequest("How many paid time off days do employees receive?", "Hybrid", 4));
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<QueryResponse>(TestAuth.Json);
        Assert.NotNull(payload);
        Assert.False(payload!.Refused, "Employee should get an answer.");
        Assert.NotEmpty(payload.Citations);
    }

    [Fact]
    public async Task Query_MissingBody_ReturnsValidationProblem()
    {
        var client = await _factory.AuthenticatedClientAsync(
            "alice", ["employee"], ["hr"], Classification.Internal);
        var response = await client.PostAsJsonAsync("/api/v1/query", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Query", body);
    }

    [Fact]
    public async Task Query_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/query", new QueryRequest("anything"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Query_RestrictedDoc_LeaksNothing()
    {
        var client = await _factory.AuthenticatedClientAsync(
            "bob", ["employee"], ["hr"], Classification.Internal);
        var response = await client.PostAsJsonAsync("/api/v1/query", new QueryRequest("What is the CEO LTIP pool for the current fiscal year?", "Hybrid", 4));
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<QueryResponse>(TestAuth.Json);
        Assert.NotNull(payload);

        foreach (var citation in payload!.Citations)
        {
            Assert.DoesNotContain("Board-only", citation.DocumentTitle, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("M&A", citation.DocumentTitle, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("3.5 million", payload.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LTIP", payload.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Documents_BoardUser_SeesRestrictedDoc()
    {
        var client = await _factory.AuthenticatedClientAsync(
            "board-user", ["employee", "board"], [], Classification.Restricted);
        var response = await client.GetAsync("/api/v1/documents?pageSize=100");
        response.EnsureSuccessStatusCode();
        var docs = await response.Content.ReadFromJsonAsync<DocumentResponse[]>(TestAuth.Json);
        Assert.NotNull(docs);
        Assert.Contains(docs!, d => d.Classification == "Restricted" && d.Title.Contains("Board", StringComparison.OrdinalIgnoreCase));
    }
}
