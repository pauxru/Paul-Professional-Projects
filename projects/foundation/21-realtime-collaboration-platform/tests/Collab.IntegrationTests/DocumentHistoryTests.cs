using System.Net.Http.Json;
using Collab.Application.Contracts;
using Collab.Domain.Documents;
using Collab.IntegrationTests.Harness;

namespace Collab.IntegrationTests;

/// <summary>
/// Versioning surface: line diff correctness, time-travel reads, and restore that creates a NEW
/// forward version (history is never rewritten).
/// </summary>
public sealed class DocumentHistoryTests(CollabAppFactory factory) : IClassFixture<CollabAppFactory>
{
    private readonly CollabAppFactory _factory = factory;

    [Fact]
    public async Task Diff_reports_inserted_content_between_sequences()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        await using var client = CollabClient.Create(_factory, scenario.Owner.Token, "author");
        await client.StartAsync(cts.Token);
        await client.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);

        var s1 = (await client.TypeAsync(0, "release checklist", cts.Token)).Sequence;

        var diff = await GetDiffAsync(http, scenario.Owner.Token, scenario.DocumentId, 0, s1, cts.Token);
        var inserted = string.Concat(diff.Segments.Where(s => s.Kind == "Insert").Select(s => s.Text));
        Assert.Contains("release checklist", inserted);

        var noChange = await GetDiffAsync(http, scenario.Owner.Token, scenario.DocumentId, s1, s1, cts.Token);
        Assert.DoesNotContain(noChange.Segments, s => s.Kind is "Insert" or "Delete");
    }

    [Fact]
    public async Task Time_travel_reads_the_document_at_an_earlier_sequence()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        await using var client = CollabClient.Create(_factory, scenario.Owner.Token, "author");
        await client.StartAsync(cts.Token);
        await client.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);

        var s1 = (await client.TypeAsync(0, "draft", cts.Token)).Sequence;
        var s2 = (await client.TypeAsync(client.Text.Length, " final", cts.Token)).Sequence;

        Assert.Equal("draft", await GetContentAtAsync(http, scenario.Owner.Token, scenario.DocumentId, s1, cts.Token));
        Assert.Equal("draft final", await GetContentAtAsync(http, scenario.Owner.Token, scenario.DocumentId, s2, cts.Token));
    }

    [Fact]
    public async Task Restore_creates_a_new_forward_version_without_rewriting_history()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        await using var client = CollabClient.Create(_factory, scenario.Owner.Token, "author");
        await client.StartAsync(cts.Token);
        await client.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);

        var s1 = (await client.TypeAsync(0, "v1", cts.Token)).Sequence;
        var s2 = (await client.TypeAsync(client.Text.Length, "-v2", cts.Token)).Sequence;

        var restored = await RestoreAsync(http, scenario.Owner.Token, scenario.DocumentId, s1, cts.Token);
        Assert.Equal("v1", restored.Content);
        // Restore moved the document FORWARD (new sequence), it did not roll the log back.
        Assert.True(restored.Sequence > s2);

        // The intermediate version is still readable — history is intact.
        Assert.Equal("v1-v2", await GetContentAtAsync(http, scenario.Owner.Token, scenario.DocumentId, s2, cts.Token));
    }

    private static async Task<DiffResultDto> GetDiffAsync(HttpClient http, string token, Guid documentId, long from, long to, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/documents/{documentId}/diff?from={from}&to={to}");
        request.Headers.Authorization = new("Bearer", token);
        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DiffResultDto>(ct))!;
    }

    private static async Task<string> GetContentAtAsync(HttpClient http, string token, Guid documentId, long sequence, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/documents/{documentId}/at/{sequence}");
        request.Headers.Authorization = new("Bearer", token);
        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DocumentContentDto>(ct))!.Content;
    }

    private static async Task<DocumentContentDto> RestoreAsync(HttpClient http, string token, Guid documentId, long toSequence, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/documents/{documentId}/restore")
        {
            Content = JsonContent.Create(new { toSequence })
        };
        request.Headers.Authorization = new("Bearer", token);
        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DocumentContentDto>(ct))!;
    }
}
