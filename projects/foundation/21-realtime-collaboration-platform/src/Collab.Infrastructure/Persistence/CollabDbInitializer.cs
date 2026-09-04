using Collab.Domain.Abstractions;
using Collab.Domain.Authorization;
using Collab.Domain.Documents;
using Collab.Domain.Identity;
using Collab.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Collab.Infrastructure.Persistence;

/// <summary>Fixed identifiers for the seeded demo tenant so the demo script and tokens are stable.</summary>
public static class DemoData
{
    public static readonly Guid AdaUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid GraceUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid LinusUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid WorkspaceId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid RunbookDocumentId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid ChecklistDocumentId = Guid.Parse("66666666-6666-6666-6666-666666666666");
}

/// <summary>
/// Creates the schema (SQLite, no external infra) and idempotently seeds a small demo tenant so the
/// app is usable out of the box. Safe to call on every startup — it no-ops when data already exists.
/// </summary>
public sealed class CollabDbInitializer(AppDbContext db, IClock clock)
{
    public async Task InitializeAsync(bool seedDemo, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);
        if (seedDemo)
            await SeedAsync(ct);
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct)) return;

        var ada = new User("Ada Lovelace", "ada@acme.example", clock, DemoData.AdaUserId);
        var grace = new User("Grace Hopper", "grace@acme.example", clock, DemoData.GraceUserId);
        var linus = new User("Linus Torvalds", "linus@acme.example", clock, DemoData.LinusUserId);
        db.Users.AddRange(ada, grace, linus);

        var workspace = new Workspace("Acme Operations", clock, DemoData.WorkspaceId);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.AddRange(
            new WorkspaceMember(workspace.Id, ada.Id, WorkspaceRole.Owner, clock),
            new WorkspaceMember(workspace.Id, grace.Id, WorkspaceRole.Editor, clock),
            new WorkspaceMember(workspace.Id, linus.Id, WorkspaceRole.Viewer, clock));

        db.Documents.AddRange(
            new Document(workspace.Id, "Incident Runbook", DocumentType.Text, ada.Id, clock, DemoData.RunbookDocumentId),
            new Document(workspace.Id, "Release Checklist", DocumentType.Structured, ada.Id, clock, DemoData.ChecklistDocumentId));

        await db.SaveChangesAsync(ct);
    }
}
