using Collab.Domain.Documents;
using Collab.IntegrationTests.Harness;
using Microsoft.AspNetCore.SignalR;

namespace Collab.IntegrationTests;

/// <summary>Hub authorization: a Viewer cannot edit and a non-member cannot even join.</summary>
public sealed class HubAuthorizationTests(CollabAppFactory factory) : IClassFixture<CollabAppFactory>
{
    private readonly CollabAppFactory _factory = factory;

    [Fact]
    public async Task Viewer_can_join_but_cannot_submit_an_edit()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        await using var viewer = CollabClient.Create(_factory, scenario.Viewer.Token, "viewer");
        await viewer.StartAsync(cts.Token);

        // Viewer has read access, so joining succeeds.
        await viewer.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);

        // But an edit is rejected with a well-defined error, not silently dropped.
        await Assert.ThrowsAsync<HubException>(() => viewer.TypeAsync(0, "nope", cts.Token));

        await viewer.WaitUntilAsync(() => !viewer.Rejections.IsEmpty, cts.Token);
        Assert.Contains(viewer.Rejections, r => r.Code == "forbidden");
    }

    [Fact]
    public async Task Non_member_cannot_join_a_document()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        await using var outsider = CollabClient.Create(_factory, scenario.Outsider.Token, "outsider");
        await outsider.StartAsync(cts.Token);

        await Assert.ThrowsAsync<HubException>(() => outsider.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token));
    }

    [Fact]
    public async Task Unauthenticated_connection_is_refused()
    {
        using var cts = TestData.Deadline();

        // No access token → the hub's [Authorize] rejects the connection during negotiation.
        await using var anonymous = CollabClient.Create(_factory, token: "", replicaId: "anon");
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync(cts.Token));
    }
}
