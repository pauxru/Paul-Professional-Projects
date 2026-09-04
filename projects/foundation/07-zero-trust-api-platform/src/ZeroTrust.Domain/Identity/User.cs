using ZeroTrust.Domain.Common;

namespace ZeroTrust.Domain.Identity;

public sealed class User : Entity
{
    public string Subject { get; private set; } = default!;
    public string Email { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public string PasswordHash { get; private set; } = default!;
    public string PasswordSalt { get; private set; } = default!;
    public string Roles { get; private set; } = string.Empty;
    public bool MfaEnrolled { get; private set; }

    private User() { }

    public User(string subject, string email, string displayName, string passwordHash, string passwordSalt, string roles, bool mfaEnrolled)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new DomainException("subject required");
        if (string.IsNullOrWhiteSpace(email)) throw new DomainException("email required");
        Subject = subject;
        Email = email;
        DisplayName = displayName;
        PasswordHash = passwordHash;
        PasswordSalt = passwordSalt;
        Roles = roles;
        MfaEnrolled = mfaEnrolled;
    }

    public IEnumerable<string> RoleList() =>
        (Roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
