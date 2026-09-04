using System.Net.Http.Json;
using Collab.Application.Contracts;
using Collab.Domain.Documents;
using Collab.IntegrationTests.Harness;

namespace Collab.IntegrationTests;

/// <summary>
/// End-to-end convergence and ordering guarantees exercised with real SignalR clients against the
/// in-memory test server. Every test carries a hard deadline so the suite always terminates.
/// </summary>
public sealed class RealtimeCollaborationTests(CollabAppFactory factory) : IClassFixture<CollabAppFactory>
{
    private readonly CollabAppFactory _factory = factory;

    [Fact]
    public async Task Two_clients_co_editing_a_text_document_converge()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        await using var ada = CollabClient.Create(_factory, scenario.Owner.Token, "ada");
        await using var grace = CollabClient.Create(_factory, scenario.Editor.Token, "grace");
        await ada.StartAsync(cts.Token);
        await grace.StartAsync(cts.Token);
        await ada.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);
        await grace.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);

        await ada.TypeAsync(0, "Runbook", cts.Token);
        await grace.WaitUntilAsync(() => grace.RemoteBatchCount >= 1, cts.Token);

        // Grace appends after the shared text she just received.
        await grace.TypeAsync(grace.Text.Length, " v2", cts.Token);
        await ada.WaitUntilAsync(() => ada.RemoteBatchCount >= 1, cts.Token);

        Assert.Equal(ada.Text, grace.Text);
        Assert.Equal("Runbook v2", ada.Text);
        Assert.Equal("Runbook v2", await ReadServerContentAsync(http, scenario.Owner.Token, scenario.DocumentId, cts.Token));
    }

    [Fact]
    public async Task Concurrent_inserts_at_the_same_position_converge_identically()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        await using var a = CollabClient.Create(_factory, scenario.Owner.Token, "aaa");
        await using var b = CollabClient.Create(_factory, scenario.Editor.Token, "bbb");
        await a.StartAsync(cts.Token);
        await b.StartAsync(cts.Token);
        await a.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);
        await b.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);

        // Both insert at position 0 without having seen each other's edit: a genuine concurrent edit.
        var ta = a.TypeAsync(0, "AAA", cts.Token);
        var tb = b.TypeAsync(0, "BBB", cts.Token);
        await Task.WhenAll(ta, tb);

        await a.WaitUntilAsync(() => a.RemoteBatchCount >= 1, cts.Token);
        await b.WaitUntilAsync(() => b.RemoteBatchCount >= 1, cts.Token);

        Assert.Equal(a.Text, b.Text);
        Assert.Equal(6, a.Text.Length);
        // Deterministic RGA tie-break (descending id → replica "bbb" before "aaa").
        Assert.Equal("BBBAAA", a.Text);
        Assert.Equal(a.Text, await ReadServerContentAsync(http, scenario.Owner.Token, scenario.DocumentId, cts.Token));
    }

    [Fact]
    public async Task Server_assigns_contiguous_sequences_under_concurrent_submits()
    {
        using var cts = TestData.Deadline(45);
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        await using var a = CollabClient.Create(_factory, scenario.Owner.Token, "a");
        await using var b = CollabClient.Create(_factory, scenario.Editor.Token, "b");
        await a.StartAsync(cts.Token);
        await b.StartAsync(cts.Token);
        await a.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);
        await b.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);

        const int perClient = 12;
        async Task Fire(CollabClient client)
        {
            for (var i = 0; i < perClient; i++)
                await client.TypeAsync(0, "x", cts.Token);
        }
        await Task.WhenAll(Fire(a), Fire(b));

        await a.WaitUntilAsync(() => a.RemoteBatchCount >= perClient, cts.Token);
        await b.WaitUntilAsync(() => b.RemoteBatchCount >= perClient, cts.Token);

        var sequences = await ReadHistorySequencesAsync(http, scenario.Owner.Token, scenario.DocumentId, cts.Token);
        Assert.Equal(perClient * 2, sequences.Count);
        Assert.Equal(Enumerable.Range(1, perClient * 2).Select(i => (long)i), sequences.OrderBy(s => s));
        Assert.Equal(a.Text, b.Text);
        Assert.Equal(a.Text, await ReadServerContentAsync(http, scenario.Owner.Token, scenario.DocumentId, cts.Token));
    }

    [Fact]
    public async Task Concurrent_structured_field_writes_resolve_by_last_writer_wins()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Structured);

        await using var a = CollabClient.Create(_factory, scenario.Owner.Token, "a");
        await using var b = CollabClient.Create(_factory, scenario.Editor.Token, "b");
        await a.StartAsync(cts.Token);
        await b.StartAsync(cts.Token);
        await a.JoinAsync(scenario.DocumentId, DocumentType.Structured, cts.Token);
        await b.JoinAsync(scenario.DocumentId, DocumentType.Structured, cts.Token);

        await a.SetFieldAsync("task-1", "status", "in-progress", cts.Token, addItem: true);
        await b.WaitUntilAsync(() => b.RemoteBatchCount >= 1, cts.Token);
        await b.SetFieldAsync("task-1", "status", "done", cts.Token);
        await a.WaitUntilAsync(() => a.RemoteBatchCount >= 1, cts.Token);

        Assert.Equal(a.StructuredJson, b.StructuredJson);
        Assert.Contains("done", a.StructuredJson);
    }

    private static async Task<string> ReadServerContentAsync(HttpClient http, string token, Guid documentId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/documents/{documentId}");
        request.Headers.Authorization = new("Bearer", token);
        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<DocumentContentDto>(ct);
        return dto!.Content;
    }

    private static async Task<IReadOnlyList<long>> ReadHistorySequencesAsync(HttpClient http, string token, Guid documentId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/documents/{documentId}/history?page=1&pageSize=200");
        request.Headers.Authorization = new("Bearer", token);
        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<PagedResult<OperationLogDto>>(ct);
        return page!.Items.Select(i => i.Sequence).ToArray();
    }
}
