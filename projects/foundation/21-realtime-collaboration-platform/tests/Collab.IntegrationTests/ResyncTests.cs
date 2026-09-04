using Collab.Domain.Documents;
using Collab.IntegrationTests.Harness;

namespace Collab.IntegrationTests;

/// <summary>
/// Reconnection and catch-up: a client that missed operations while offline resyncs correctly from
/// either a full checkpoint or an incremental operation list, and ends identical to the server.
/// </summary>
public sealed class ResyncTests(CollabAppFactory factory) : IClassFixture<CollabAppFactory>
{
    private readonly CollabAppFactory _factory = factory;

    [Fact]
    public async Task Offline_client_catches_up_from_checkpoint_then_incrementally()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        await using var author = CollabClient.Create(_factory, scenario.Owner.Token, "author");
        await author.StartAsync(cts.Token);
        await author.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);

        // The author makes three edits while the second client is completely absent.
        await author.TypeAsync(0, "abc", cts.Token);

        // A second client comes online but never joins the live group (still "offline" from the stream).
        await using var latecomer = CollabClient.Create(_factory, scenario.Editor.Token, "late");
        await latecomer.StartAsync(cts.Token);
        latecomer.Track(scenario.DocumentId, DocumentType.Text);

        // Full resync from scratch reconstructs the current document from the checkpoint.
        var full = await latecomer.ResyncAsync(0, cts.Token);
        Assert.True(full.Full);
        latecomer.ApplyResync(full);
        Assert.Equal("abc", latecomer.Text);

        // The author edits again while the latecomer is still not in the group: K missed operations.
        await author.TypeAsync(author.Text.Length, "def", cts.Token);

        // Incremental resync delivers exactly the missed operations.
        var incremental = await latecomer.ResyncAsync(full.CurrentSequence, cts.Token);
        Assert.False(incremental.Full);
        Assert.NotEmpty(incremental.Operations);
        latecomer.ApplyResync(incremental);

        Assert.Equal("abcdef", latecomer.Text);
        Assert.Equal(author.Text, latecomer.Text);
    }

    [Fact]
    public async Task Joining_returns_the_authoritative_current_state()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        await using var author = CollabClient.Create(_factory, scenario.Owner.Token, "author");
        await author.StartAsync(cts.Token);
        await author.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);
        await author.TypeAsync(0, "hello world", cts.Token);

        await using var joiner = CollabClient.Create(_factory, scenario.Editor.Token, "joiner");
        await joiner.StartAsync(cts.Token);
        var state = await joiner.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);

        Assert.Equal("hello world", state.Content);
        Assert.Equal("hello world", joiner.Text);
        Assert.True(state.Sequence >= 1);
    }
}
