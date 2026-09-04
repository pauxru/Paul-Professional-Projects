using System.Net;
using System.Net.Sockets;

namespace AgentPlatform.Domain.Security;

/// <summary>Result of evaluating an outbound URL against the SSRF policy.</summary>
public sealed record UrlGuardResult(bool Allowed, string? Reason)
{
    public static UrlGuardResult Ok { get; } = new(true, null);
    public static UrlGuardResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// Server-Side Request Forgery guard for the <c>http_get</c> tool. Only requests to an
/// explicit host allow-list are permitted, only over http/https, and any URL whose host
/// resolves (or is literally) a loopback, private, link-local, unique-local or otherwise
/// non-public address is rejected. Redirects are handled by the caller, which re-checks
/// every hop against this guard (no off-list redirects).
/// </summary>
public sealed class UrlSecurityPolicy
{
    private readonly HashSet<string> _allowedHosts;

    public UrlSecurityPolicy(IEnumerable<string> allowedHosts)
        => _allowedHosts = new HashSet<string>(allowedHosts.Select(h => h.Trim().ToLowerInvariant()), StringComparer.Ordinal);

    public IReadOnlyCollection<string> AllowedHosts => _allowedHosts;

    /// <summary>Check a URL string. Resolver lets tests inject deterministic DNS answers.</summary>
    public UrlGuardResult Check(string url, Func<string, IReadOnlyList<IPAddress>>? resolver = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return UrlGuardResult.Deny("URL is not an absolute URI.");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return UrlGuardResult.Deny($"Scheme '{uri.Scheme}' is not permitted (http/https only).");

        if (uri.IsDefaultPort is false && uri.Port is not (80 or 443 or 8080))
            return UrlGuardResult.Deny($"Port {uri.Port} is not permitted.");

        var host = uri.Host.ToLowerInvariant();
        if (!_allowedHosts.Contains(host))
            return UrlGuardResult.Deny($"Host '{host}' is not on the allow-list.");

        // Literal IP host: check directly.
        if (IPAddress.TryParse(uri.Host, out var literal))
            return IsPublic(literal) ? UrlGuardResult.Ok : UrlGuardResult.Deny($"Address {literal} is not a public address.");

        // Resolve and ensure every resolved address is public (defends against DNS rebinding).
        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = resolver is not null ? resolver(host) : Dns.GetHostAddresses(host);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return UrlGuardResult.Deny($"Host '{host}' could not be resolved.");
        }

        if (addresses.Count == 0)
            return UrlGuardResult.Deny($"Host '{host}' resolved to no addresses.");

        foreach (var address in addresses)
            if (!IsPublic(address))
                return UrlGuardResult.Deny($"Host '{host}' resolves to non-public address {address}.");

        return UrlGuardResult.Ok;
    }

    /// <summary>True only for globally-routable unicast addresses.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            // 0.0.0.0/8, 10/8, 100.64/10 (CGNAT), 127/8, 169.254/16, 172.16/12,
            // 192.0.0/24, 192.0.2/24, 192.168/16, 198.18/15, 198.51.100/24,
            // 203.0.113/24, 224/4 (multicast), 240/4 (reserved), 255.255.255.255.
            return b[0] switch
            {
                0 => false,
                10 => false,
                100 when b[1] >= 64 && b[1] <= 127 => false,
                127 => false,
                169 when b[1] == 254 => false,
                172 when b[1] >= 16 && b[1] <= 31 => false,
                192 when b[1] == 168 => false,
                192 when b[1] == 0 => false,
                198 when b[1] == 18 || b[1] == 19 => false,
                >= 224 => false,
                _ => true,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
            if (address.IsIPv4MappedToIPv6) return IsPublic(address.MapToIPv4());
            var b = address.GetAddressBytes();
            if (b[0] == 0xfc || b[0] == 0xfd) return false; // unique local fc00::/7
            if (b[0] == 0 && b[15] == 1) return false;      // ::1 loopback (belt and braces)
            return true;
        }

        return false;
    }
}
