using Collab.Application.Contracts;
using Collab.Domain.Documents;
using Collab.IntegrationTests.Harness;
using Microsoft.AspNetCore.SignalR;

namespace Collab.IntegrationTests;

/// <summary>
/// Hub abuse control: a connection that floods the server with operations past its burst and sustained
/// rate is throttled and ultimately disconnected. Uses a factory with deliberately tiny limits.
/// </summary>
public sealed class HubRateLimitTests(RateLimitedAppFactory factory) : IClassFixture<RateLimitedAppFactory>
{
    private readonly RateLimitedAppFactory _factory = factory;

    [Fact]
    public async Task Flooding_client_is_rejected_and_then_disconnected()
    {
        using var cts = TestData.Deadline();
        var http = _factory.CreateClient();
        var scenario = await TestData.CreateWorkspaceWithDocumentAsync(_factory, http, DocumentType.Text);

        await using var abuser = CollabClient.Create(_factory, scenario.Owner.Token, "abuser");
        await abuser.StartAsync(cts.Token);
        await abuser.JoinAsync(scenario.DocumentId, DocumentType.Text, cts.Token);

        // Burst = 3, sustained = 1/sec, disconnect after 3 violations. Fire well beyond that.
        var rejected = false;
        for (var i = 0; i < 50; i++)
        {
            var envelope = new OperationEnvelope(
                scenario.DocumentId, "abuser",
                new[] { new TextOpDto("insert", $"{i + 1}@abuser", "", "x") },
                Array.Empty<StructuredOpDto>(),
                ClientTag: $"op-{i}");
            try
            {
                await abuser.SubmitRawAsync(envelope, cts.Token);
            }
            catch (HubException)
            {
                rejected = true; // rate-limit rejection surfaced to the caller
            }
            catch (Exception)
            {
                rejected = true; // connection was aborted mid-flight
                break;
            }

            if (abuser.Closed) break;
        }

        Assert.True(rejected, "Expected the flooding client to be rate-limited.");

        // The server escalates repeated violations to a forced disconnect.
        await abuser.WaitUntilAsync(() => abuser.Closed, cts.Token);
        Assert.True(abuser.Closed);
        Assert.Contains(abuser.Rejections, r => r.Code == "rate_limited");
    }
}
