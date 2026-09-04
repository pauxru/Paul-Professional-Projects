using ZeroTrust.Domain.Common;

namespace ZeroTrust.Domain.Identity;

/// <summary>
/// An RSA signing key with a kid. Two active keys at a time enables JWKS rollover:
/// new tokens are signed with the "primary" key; the "secondary" remains published
/// for verification of tokens issued before rotation.
/// </summary>
public sealed class SigningKey : Entity
{
    public string Kid { get; private set; } = default!;
    public string Algorithm { get; private set; } = "RS256";
    public string PublicKeyPem { get; private set; } = default!;
    public string PrivateKeyPem { get; private set; } = default!;
    public bool IsPrimary { get; private set; }
    public DateTime NotBeforeUtc { get; private set; }
    public DateTime? RetiredAtUtc { get; private set; }

    private SigningKey() { }

    public SigningKey(string kid, string algorithm, string publicPem, string privatePem, bool isPrimary, DateTime notBefore)
    {
        Kid = kid;
        Algorithm = algorithm;
        PublicKeyPem = publicPem;
        PrivateKeyPem = privatePem;
        IsPrimary = isPrimary;
        NotBeforeUtc = notBefore;
    }

    public void Demote() => IsPrimary = false;
    public void Promote() => IsPrimary = true;
    public void Retire(DateTime nowUtc) => RetiredAtUtc = nowUtc;
    public bool IsActive(DateTime nowUtc) => RetiredAtUtc is null && NotBeforeUtc <= nowUtc;
}
