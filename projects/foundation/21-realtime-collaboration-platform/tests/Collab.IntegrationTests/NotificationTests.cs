using System.Net;
using System.Net.Http.Json;
using Collab.Application.Contracts;
using Collab.Domain.Documents;
using Collab.IntegrationTests.Harness;

namespace Collab.IntegrationTests;

/// <summary>
/// Mentions notify a connected user live over the hub and are persisted to the inbox for a user who
/// is offline, who then reads them on return.
/// </summary>
public sealed class NotificationTests(CollabAppFactory factory) : IClassFixture<CollabAppFactory>
{
    private readonly CollabAppFactory _factory = factory;

    [Fact]
    public async Task Mention_is_delivered_live_to_a_connected_user()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        // The editor is connected to the hub (not necessarily in the document group).
        await using var editor = CollabClient.Create(_factory, scenario.Editor.Token, "editor");
        await editor.StartAsync(cts.Token);

        // The owner mentions the editor in a comment.
        await PostCommentAsync(http, scenario.Owner.Token, scenario.DocumentId,
            body: "please review @editor", mentions: new[] { scenario.Editor.UserId });

        await editor.WaitUntilAsync(() => editor.NotificationEvents.Any(n => n.Type == "Mention"), cts.Token);
        Assert.Contains(editor.NotificationEvents, n => n.Type == "Mention" && n.DocumentId == scenario.DocumentId);
    }

    [Fact]
    public async Task Mention_is_persisted_for_an_offline_user_and_read_on_return()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        // The viewer is offline (no hub connection) when mentioned.
        await PostCommentAsync(http, scenario.Owner.Token, scenario.DocumentId,
            body: "heads up @viewer", mentions: new[] { scenario.Viewer.UserId });

        // On return, the viewer finds the notification persisted and unread.
        var unread = await ReadNotificationsAsync(http, scenario.Viewer.Token, unreadOnly: true, cts.Token);
        Assert.True(unread.TotalCount >= 1);
        var mention = Assert.Single(unread.Items, n => n.Type == "Mention");
        Assert.False(mention.IsRead);

        // Marking it read empties the unread inbox.
        await MarkReadAsync(http, scenario.Viewer.Token, mention.Id, cts.Token);
        var afterRead = await ReadNotificationsAsync(http, scenario.Viewer.Token, unreadOnly: true, cts.Token);
        Assert.DoesNotContain(afterRead.Items, n => n.Id == mention.Id);
    }

    private static async Task PostCommentAsync(HttpClient http, string token, Guid documentId, string body, IReadOnlyList<Guid> mentions)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/documents/{documentId}/comments")
        {
            Content = JsonContent.Create(new
            {
                body,
                anchorStart = 0,
                anchorEnd = 0,
                fieldPath = (string?)null,
                mentions
            })
        };
        request.Headers.Authorization = new("Bearer", token);
        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<PagedResult<NotificationDto>> ReadNotificationsAsync(HttpClient http, string token, bool unreadOnly, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/notifications?unreadOnly={unreadOnly.ToString().ToLowerInvariant()}");
        request.Headers.Authorization = new("Bearer", token);
        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PagedResult<NotificationDto>>(ct))!;
    }

    private static async Task MarkReadAsync(HttpClient http, string token, Guid notificationId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/notifications/{notificationId}/read");
        request.Headers.Authorization = new("Bearer", token);
        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
