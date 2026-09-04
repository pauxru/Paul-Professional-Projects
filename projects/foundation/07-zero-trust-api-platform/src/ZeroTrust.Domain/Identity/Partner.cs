using ZeroTrust.Domain.Common;

namespace ZeroTrust.Domain.Identity;

public sealed class Partner : Entity
{
    public string PartnerCode { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public string ClientId { get; private set; } = default!;
    public string ClientSecretHash { get; private set; } = default!;
    public string ClientSecretSalt { get; private set; } = default!;
    public string AllowedScopes { get; private set; } = string.Empty;
    public string AllowedIps { get; private set; } = string.Empty;
    public string ClientCertThumbprint { get; private set; } = string.Empty;
    public int RateLimitPermitsPerMinute { get; private set; } = 60;
    public bool Enabled { get; private set; } = true;

    private Partner() { }

    public Partner(string partnerCode, string displayName, string clientId, string clientSecretHash,
        string clientSecretSalt, string allowedScopes, string allowedIps, string clientCertThumbprint,
        int rateLimit, bool enabled)
    {
        PartnerCode = partnerCode;
        DisplayName = displayName;
        ClientId = clientId;
        ClientSecretHash = clientSecretHash;
        ClientSecretSalt = clientSecretSalt;
        AllowedScopes = allowedScopes;
        AllowedIps = allowedIps;
        ClientCertThumbprint = clientCertThumbprint;
        RateLimitPermitsPerMinute = rateLimit;
        Enabled = enabled;
    }

    public void Disable() => Enabled = false;
    public void SetRateLimit(int permitsPerMinute) => RateLimitPermitsPerMinute = permitsPerMinute;

    public IEnumerable<string> AllowedScopeList() =>
        (AllowedScopes ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsIpAllowed(string? ip) =>
        string.IsNullOrEmpty(AllowedIps) ||
        AllowedIps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(a => string.Equals(a, ip, StringComparison.OrdinalIgnoreCase) || a == "*");
}
