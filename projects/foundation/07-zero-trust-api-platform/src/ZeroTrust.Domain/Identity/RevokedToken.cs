using ZeroTrust.Domain.Common;

namespace ZeroTrust.Domain.Identity;

/// <summary>
/// Revoked JWT tracker (jti + expiry). Used for immediate revocation of access tokens
/// that would otherwise remain valid until natural expiry.
/// </summary>
public sealed class RevokedToken : Entity
{
    public string Jti { get; private set; } = default!;
    public string Subject { get; private set; } = default!;
    public DateTime ExpiresAtUtc { get; private set; }
    public string Reason { get; private set; } = string.Empty;

    private RevokedToken() { }

    public RevokedToken(string jti, string subject, DateTime expiresAtUtc, string reason)
    {
        Jti = jti;
        Subject = subject;
        ExpiresAtUtc = expiresAtUtc;
        Reason = reason;
    }
}
