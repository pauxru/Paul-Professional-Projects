using ZeroTrust.Domain.Common;

namespace ZeroTrust.Domain.Identity;

/// <summary>
/// Legacy API key kept for a dual-accept migration window. Salted hash storage.
/// After DeprecatedAfterUtc, the key is rejected in enforcement mode.
/// </summary>
public sealed class ApiKey : Entity
{
    public string KeyId { get; private set; } = default!;
    public string KeyHash { get; private set; } = default!;
    public string KeySalt { get; private set; } = default!;
    public string OwnerPartnerCode { get; private set; } = default!;
    public string AllowedScopes { get; private set; } = string.Empty;
    public DateTime? DeprecatedAfterUtc { get; private set; }
    public bool Revoked { get; private set; }
    public DateTime? LastUsedAtUtc { get; private set; }
    public int UsageCount { get; private set; }

    private ApiKey() { }

    public ApiKey(string keyId, string keyHash, string keySalt, string ownerPartnerCode,
        string allowedScopes, DateTime? deprecatedAfterUtc)
    {
        KeyId = keyId;
        KeyHash = keyHash;
        KeySalt = keySalt;
        OwnerPartnerCode = ownerPartnerCode;
        AllowedScopes = allowedScopes;
        DeprecatedAfterUtc = deprecatedAfterUtc;
    }

    public void RecordUse(DateTime nowUtc)
    {
        LastUsedAtUtc = nowUtc;
        UsageCount++;
    }

    public void Revoke() => Revoked = true;

    public bool IsUsable(DateTime nowUtc, bool enforcementActive) =>
        !Revoked && (!enforcementActive || DeprecatedAfterUtc is null || DeprecatedAfterUtc > nowUtc);
}
