using Collab.Domain.Abstractions;

namespace Collab.Domain.Workspaces;

/// <summary>A collaborative workspace that owns documents and has members with roles.</summary>
public sealed class Workspace
{
    private Workspace() { }

    public Workspace(string name, IClock clock, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Name = name;
        CreatedAt = UpdatedAt = clock.UtcNow;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Rename(string name, IClock clock)
    {
        Name = name;
        UpdatedAt = clock.UtcNow;
    }
}
