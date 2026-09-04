using Collab.Domain.Abstractions;

namespace Collab.Domain.Identity;

/// <summary>A user account. Authentication maps a JWT subject claim to <see cref="Id"/>.</summary>
public sealed class User
{
    private User() { }

    public User(string displayName, string email, IClock clock, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        DisplayName = displayName;
        Email = email;
        CreatedAt = clock.UtcNow;
    }

    public Guid Id { get; private set; }
    public string DisplayName { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
}
