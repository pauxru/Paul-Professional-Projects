using System.Net.Http.Json;
using Collab.Application.Contracts;
using Collab.Application.Services;
using Collab.Domain.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Collab.IntegrationTests.Harness;

/// <summary>An authenticated test principal (user id + bearer token).</summary>
public sealed record Principal(Guid UserId, string Email, string DisplayName, string Token);

/// <summary>A ready-to-use collaboration scenario: a workspace with members and one document.</summary>
public sealed record Scenario(
    Guid WorkspaceId,
    Guid DocumentId,
    Principal Owner,
    Principal Editor,
    Principal Viewer,
    Principal Outsider);

/// <summary>Helpers to provision auth, workspaces, documents and memberships for integration tests.</summary>
public static class TestData
{
    private sealed record TokenResponse(string Token, Guid UserId, string Email, string DisplayName, DateTimeOffset ExpiresAt);

    /// <summary>Exchange an email for a JWT (provisioning the user on first sight).</summary>
    public static async Task<Principal> SignInAsync(HttpClient http, string email, string? displayName = null)
    {
        var response = await http.PostAsJsonAsync("/api/v1/auth/token", new { email, displayName });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>()
            ?? throw new InvalidOperationException("Token endpoint returned no body.");
        return new Principal(body.UserId, body.Email, body.DisplayName, body.Token);
    }

    /// <summary>
    /// Create a workspace owned by <paramref name="owner"/>, add an Editor, Commenter/Viewer, and a
    /// document of the requested type. The "outsider" is a provisioned user with no membership.
    /// </summary>
    public static async Task<Scenario> CreateWorkspaceWithDocumentAsync(
        CollabAppFactory factory, HttpClient http, DocumentType type, string? seedTitle = null)
    {
        var suffix = Guid.NewGuid().ToString("n")[..8];
        var owner = await SignInAsync(http, $"owner-{suffix}@acme.example", "Owner");
        var editor = await SignInAsync(http, $"editor-{suffix}@acme.example", "Editor");
        var viewer = await SignInAsync(http, $"viewer-{suffix}@acme.example", "Viewer");
        var outsider = await SignInAsync(http, $"outsider-{suffix}@acme.example", "Outsider");

        var (workspaceId, documentId) = await factory.InScopeAsync(async sp =>
        {
            var workspaces = sp.GetRequiredService<WorkspaceService>();
            var documents = sp.GetRequiredService<DocumentService>();

            var ws = await workspaces.CreateAsync(owner.UserId, new CreateWorkspaceRequest($"WS-{suffix}"), default);
            await workspaces.AddMemberAsync(owner.UserId, ws.Id, new AddMemberRequest(editor.UserId, "Editor"), default);
            await workspaces.AddMemberAsync(owner.UserId, ws.Id, new AddMemberRequest(viewer.UserId, "Viewer"), default);

            var typeName = type == DocumentType.Text ? "text" : "structured";
            var doc = await documents.CreateAsync(owner.UserId, new CreateDocumentRequest(ws.Id, seedTitle ?? $"Doc-{suffix}", typeName), default);
            return (ws.Id, doc.Id);
        });

        return new Scenario(workspaceId, documentId, owner, editor, viewer, outsider);
    }

    /// <summary>A short, absolute deadline for realtime waits so the suite always terminates.</summary>
    public static CancellationTokenSource Deadline(int seconds = 30) => new(TimeSpan.FromSeconds(seconds));
}
