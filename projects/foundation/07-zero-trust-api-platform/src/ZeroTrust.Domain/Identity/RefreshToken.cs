using ZeroTrust.Domain.Common;

namespace ZeroTrust.Domain.Identity;

/// <summary>
/// Refresh token stored server-side. Tokens belong to a "family" (rotation lineage).
/// When a token is used, a new one is issued and the old one is marked Consumed.
/// Attempting to use a consumed token (reuse) revokes the whole family (theft signal).
/// </summary>
public sealed class RefreshToken : Entity
{
    public string TokenHash { get; private set; } = default!;
    public Guid FamilyId { get; private set; }
    public string Subject { get; private set; } = default!;
    public string Audience { get; private set; } = default!;
    public string Scopes { get; private set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; private set; }
    public bool Consumed { get; private set; }
    public bool Revoked { get; private set; }
    public Guid? ReplacedByRefreshTokenId { get; private set; }

    private RefreshToken() { }

    public RefreshToken(string tokenHash, Guid familyId, string subject, string audience, string scopes, DateTime expiresAtUtc)
    {
        TokenHash = tokenHash;
        FamilyId = familyId;
        Subject = subject;
        Audience = audience;
        Scopes = scopes;
        ExpiresAtUtc = expiresAtUtc;
    }

    public void MarkConsumed(Guid replacedById)
    {
        Consumed = true;
        ReplacedByRefreshTokenId = replacedById;
    }

    public void Revoke() => Revoked = true;

    public bool IsActive(DateTime nowUtc) => !Consumed && !Revoked && ExpiresAtUtc > nowUtc;
}
