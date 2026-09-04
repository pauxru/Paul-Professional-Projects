using Collab.Application.Abstractions;
using Collab.Application.Services;
using Collab.Domain.Documents;
using Collab.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Collab.IntegrationTests;

/// <summary>
/// Proves the persistence model: after a checkpoint has been written, evicting the in-memory runtime
/// and rebuilding it from the latest snapshot + the trailing operation log reproduces the exact
/// document. Uses a factory that snapshots every 5 operations.
/// </summary>
public sealed class SnapshotReplayTests(FrequentSnapshotAppFactory factory) : IClassFixture<FrequentSnapshotAppFactory>
{
    private readonly FrequentSnapshotAppFactory _factory = factory;

    [Fact]
    public async Task Snapshot_plus_log_replay_reproduces_the_document_exactly()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        await using var client = CollabClient.Create(_factory, scenario.Owner.Token, "author");
        await client.StartAsync(cts.Token);
        await client.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);

        // Six submissions cross the every-5-ops snapshot boundary: a checkpoint at seq 5 plus a tail.
        foreach (var ch in new[] { "a", "b", "c", "d", "e", "f" })
            await client.TypeAsync(client.Text.Length, ch, cts.Token);

        Assert.Equal("abcdef", client.Text);

        // Force the runtime to be discarded, then rebuild it from persisted state alone.
        await _factory.InScopeAsync(sp =>
        {
            sp.GetRequiredService<IDocumentRuntimeCache>().Evict(scenario.DocumentId);
            return Task.CompletedTask;
        });

        var rebuilt = await _factory.InScopeAsync(async sp =>
        {
            var collaboration = sp.GetRequiredService<CollaborationService>();
            var state = await collaboration.GetStateAsync(scenario.Owner.UserId, scenario.DocumentId, cts.Token);
            return state.Content;
        });

        Assert.Equal("abcdef", rebuilt);
    }
}
