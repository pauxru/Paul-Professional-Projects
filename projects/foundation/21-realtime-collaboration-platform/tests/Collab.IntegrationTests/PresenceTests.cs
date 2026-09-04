using Collab.Application.Contracts;
using Collab.Domain.Documents;
using Collab.IntegrationTests.Harness;

namespace Collab.IntegrationTests;

/// <summary>Presence propagation across clients, including coalesced cursor updates.</summary>
public sealed class PresenceTests(CollabAppFactory factory) : IClassFixture<CollabAppFactory>
{
    private readonly CollabAppFactory _factory = factory;

    [Fact]
    public async Task Presence_propagates_when_a_second_client_joins()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        await using var a = CollabClient.Create(_factory, scenario.Owner.Token, "a");
        await using var b = CollabClient.Create(_factory, scenario.Editor.Token, "b");
        await a.StartAsync(cts.Token);
        await a.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);

        await b.StartAsync(cts.Token);
        await b.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);

        // A must eventually observe a presence snapshot that includes both participants.
        await a.WaitUntilAsync(() => a.PresenceEvents.Any(p => p.Participants.Count >= 2), cts.Token);
        var latest = a.PresenceEvents.Last(p => p.Participants.Count >= 2);
        var userIds = latest.Participants.Select(p => p.UserId).ToHashSet();
        Assert.Contains(scenario.Owner.UserId, userIds);
        Assert.Contains(scenario.Editor.UserId, userIds);
    }

    [Fact]
    public async Task Cursor_updates_are_coalesced_and_broadcast()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        await using var a = CollabClient.Create(_factory, scenario.Owner.Token, "a");
        await using var b = CollabClient.Create(_factory, scenario.Editor.Token, "b");
        await a.StartAsync(cts.Token);
        await b.StartAsync(cts.Token);
        await a.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);
        await b.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);

        // Fire a burst of cursor moves; the flusher collapses them into a coalesced broadcast.
        for (var i = 0; i < 20; i++)
            await a.UpdatePresenceAsync(i, i, cts.Token);

        await b.WaitUntilAsync(
            () => b.PresenceEvents.Any(p => p.Participants.Any(x => x.UserId == scenario.Owner.UserId && x.CursorStart == 19)),
            cts.Token);

        var owner = b.PresenceEvents
            .SelectMany(p => p.Participants)
            .Last(x => x.UserId == scenario.Owner.UserId);
        Assert.Equal(19, owner.CursorStart);
    }
}
