using System.Net;
using System.Net.Http.Json;
using Collab.Application.Contracts;
using Collab.Domain.Documents;
using Collab.IntegrationTests.Harness;

namespace Collab.IntegrationTests;

/// <summary>
/// A comment anchored to a text range follows its text as the document is edited, and is flagged as
/// orphaned when the text it points at is deleted — verified end-to-end through the engine.
/// </summary>
public sealed class CommentAnchorTests(CollabAppFactory factory) : IClassFixture<CollabAppFactory>
{
    private readonly CollabAppFactory _factory = factory;

    [Fact]
    public async Task Anchor_follows_inserted_text_then_orphans_when_its_text_is_deleted()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        await using var client = CollabClient.Create(_factory, scenario.Owner.Token, "author");
        await client.StartAsync(cts.Token);
        await client.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);

        // "Hello World" — anchor the comment on "World" [6,11).
        await client.TypeAsync(0, "Hello World", cts.Token);
        var comment = await PostAnchoredCommentAsync(http, scenario.Owner.Token, scenario.DocumentId, "re: World", 6, 11);
        Assert.Equal("TextRange", comment.AnchorKind);

        // Insert "XX" at the very start: the anchor must shift right by two to keep pointing at "World".
        await client.TypeAsync(0, "XX", cts.Token);
        await WaitForAnchorAsync(http, scenario.Owner.Token, scenario.DocumentId, comment.Id,
            c => c.AnchorStart == 8 && c.AnchorEnd == 13 && !c.IsOrphaned, cts.Token);

        // Now delete "World" (now at offset 8..12): the comment loses its text and is orphaned.
        await client.DeleteAsync(8, 5, cts.Token);
        await WaitForAnchorAsync(http, scenario.Owner.Token, scenario.DocumentId, comment.Id,
            c => c.IsOrphaned, cts.Token);
    }

    private static async Task<CommentDto> PostAnchoredCommentAsync(HttpClient http, string token, Guid documentId, string body, int start, int end)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/documents/{documentId}/comments")
        {
            Content = JsonContent.Create(new { body, anchorStart = start, anchorEnd = end, fieldPath = (string?)null, mentions = Array.Empty<Guid>() })
        };
        request.Headers.Authorization = new("Bearer", token);
        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CommentDto>())!;
    }

    private static async Task WaitForAnchorAsync(HttpClient http, string token, Guid documentId, Guid commentId, Func<CommentDto, bool> predicate, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/documents/{documentId}/comments");
            request.Headers.Authorization = new("Bearer", token);
            var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var comments = await response.Content.ReadFromJsonAsync<List<CommentDto>>(ct);
            var comment = comments!.FirstOrDefault(c => c.Id == commentId);
            if (comment is not null && predicate(comment)) return;
            await Task.Delay(25, ct);
        }
    }
}
