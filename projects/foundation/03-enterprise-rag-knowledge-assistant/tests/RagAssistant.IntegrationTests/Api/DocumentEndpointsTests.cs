using System.Net;
using System.Net.Http.Json;
using RagAssistant.Api.Contracts;
using RagAssistant.Domain.Documents;
using RagAssistant.IntegrationTests.Fixtures;

namespace RagAssistant.IntegrationTests.Api;

public sealed class DocumentEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public DocumentEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task IngestDocument_Idempotent_ReturnsExistingOnRepeat()
    {
        var client = await _factory.AuthenticatedClientAsync(
            "admin", ["employee", "admin"], ["hr"], Classification.Confidential);

        var request = new IngestDocumentRequest(
            Title: "Test Policy",
            Source: "test/policy.md",
            Content: "Employees must complete safety training annually. Training is provided by the operations team.",
            Classification: "Internal",
            Roles: new[] { "employee" },
            Departments: new[] { "hr" },
            Strategy: "SentenceAware");

        var firstResponse = await client.PostAsJsonAsync("/api/v1/documents", request);
        var second = await client.PostAsJsonAsync("/api/v1/documents", request);

        Assert.True(firstResponse.IsSuccessStatusCode);
        Assert.True(second.IsSuccessStatusCode);

        var first = await firstResponse.Content.ReadFromJsonAsync<DocumentResponse>(TestAuth.Json);
        var repeat = await second.Content.ReadFromJsonAsync<DocumentResponse>(TestAuth.Json);
        Assert.Equal(first!.Id, repeat!.Id);
    }

    [Fact]
    public async Task ListDocuments_FiltersByAcl()
    {
        var employeeOnly = await _factory.AuthenticatedClientAsync(
            "carol", ["employee"], ["engineering"], Classification.Internal);
        var response = await employeeOnly.GetAsync("/api/v1/documents?page=1&pageSize=50");
        response.EnsureSuccessStatusCode();
        var docs = await response.Content.ReadFromJsonAsync<DocumentResponse[]>(TestAuth.Json);
        Assert.NotNull(docs);
        Assert.DoesNotContain(docs!, d => d.Classification == "Restricted");
    }

    [Fact]
    public async Task GetDocument_UnauthorizedClassification_Returns403()
    {
        var admin = await _factory.AuthenticatedClientAsync(
            "board", ["employee", "board", "admin"], [], Classification.Restricted);
        var listResponse = await admin.GetAsync("/api/v1/documents?pageSize=100");
        listResponse.EnsureSuccessStatusCode();
        var docs = await listResponse.Content.ReadFromJsonAsync<DocumentResponse[]>(TestAuth.Json);
        var restrictedDoc = Assert.Single(docs!, d => d.Classification == "Restricted" && d.Title.Contains("Board"));

        var unauthorized = await _factory.AuthenticatedClientAsync(
            "outsider", ["employee"], [], Classification.Internal);
        var response = await unauthorized.GetAsync($"/api/v1/documents/{restrictedDoc.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task IngestDocument_MissingContent_ReturnsValidationProblem()
    {
        var client = await _factory.AuthenticatedClientAsync(
            "admin", ["employee", "admin"], ["hr"], Classification.Internal);

        var response = await client.PostAsJsonAsync("/api/v1/documents", new IngestDocumentRequest(
            Title: "",
            Source: "",
            Content: "",
            Classification: "Internal",
            Roles: null,
            Departments: null,
            Strategy: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
