using System.Net;
using System.Net.Http.Json;
using Collab.Application.Contracts;
using Collab.Domain.Documents;
using Collab.IntegrationTests.Harness;

namespace Collab.IntegrationTests;

/// <summary>REST contract: authentication, authorization (IDOR protection) and input validation.</summary>
public sealed class RestApiTests(CollabAppFactory factory) : IClassFixture<CollabAppFactory>
{
    private readonly CollabAppFactory _factory = factory;

    [Fact]
    public async Task Unauthenticated_request_is_401()
    {
        var http = _factory.CreateClient();
        var response = await http.GetAsync("/api/v1/workspaces");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Non_member_reading_a_document_is_403()
    {
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/documents/{scenario.DocumentId}");
        request.Headers.Authorization = new("Bearer", scenario.Outsider.Token);
        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsShape>();
        Assert.Equal(403, problem!.Status);
    }

    [Fact]
    public async Task Creating_a_document_with_an_invalid_title_is_400()
    {
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = JsonContent.Create(new { workspaceId = scenario.WorkspaceId, title = "", type = "text" })
        };
        request.Headers.Authorization = new("Bearer", scenario.Owner.Token);
        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Creating_a_document_with_an_unknown_type_is_400()
    {
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = JsonContent.Create(new { workspaceId = scenario.WorkspaceId, title = "Valid", type = "spreadsheet" })
        };
        request.Headers.Authorization = new("Bearer", scenario.Owner.Token);
        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Workspace_and_document_are_listed_for_a_member()
    {
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        using var wsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/workspaces");
        wsRequest.Headers.Authorization = new("Bearer", scenario.Editor.Token);
        var workspaces = await (await http.SendAsync(wsRequest)).Content.ReadFromJsonAsync<List<WorkspaceDto>>();
        Assert.Contains(workspaces!, w => w.Id == scenario.WorkspaceId);

        using var docRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/documents?workspaceId={scenario.WorkspaceId}");
        docRequest.Headers.Authorization = new("Bearer", scenario.Editor.Token);
        var documents = await (await http.SendAsync(docRequest)).Content.ReadFromJsonAsync<PagedResult<DocumentDto>>();
        Assert.Contains(documents!.Items, d => d.Id == scenario.DocumentId);
    }

    [Fact]
    public async Task Health_endpoints_report_ready()
    {
        var http = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/ready")).StatusCode);
    }

    private sealed record ProblemDetailsShape(string? Title, int Status);
}
