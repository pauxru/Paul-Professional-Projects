using System.Net;
using AgentPlatform.Domain.Security;

namespace AgentPlatform.UnitTests.Security;

/// <summary>
/// Proves the SSRF guard for <c>http_get</c>: only allow-listed hosts that resolve to public
/// addresses are permitted, and loopback/private/link-local targets (incl. DNS-rebinding) are denied.
/// </summary>
public sealed class UrlSecurityPolicyTests
{
    private static readonly UrlSecurityPolicy Policy = new(new[] { "example.com", "api.example.com" });

    private static Func<string, IReadOnlyList<IPAddress>> Resolve(params string[] ips)
        => _ => ips.Select(IPAddress.Parse).ToArray();

    [Fact]
    public void Allows_listed_host_resolving_to_public_address()
    {
        var result = Policy.Check("https://example.com/data", Resolve("93.184.216.34"));
        Assert.True(result.Allowed, result.Reason);
    }

    [Fact]
    public void Denies_host_not_on_allow_list()
    {
        var result = Policy.Check("https://evil.example/x", Resolve("93.184.216.34"));
        Assert.False(result.Allowed);
        Assert.Contains("allow-list", result.Reason);
    }

    [Fact]
    public void Denies_non_http_scheme()
    {
        var result = Policy.Check("ftp://example.com/x");
        Assert.False(result.Allowed);
    }

    [Theory]
    [InlineData("127.0.0.1")]     // loopback
    [InlineData("10.0.0.5")]      // private
    [InlineData("192.168.1.10")]  // private
    [InlineData("169.254.1.1")]   // link-local
    [InlineData("172.16.0.1")]    // private
    public void Denies_dns_rebinding_to_internal_address(string internalIp)
    {
        var result = Policy.Check("https://example.com/", Resolve(internalIp));
        Assert.False(result.Allowed);
        Assert.Contains("non-public", result.Reason);
    }

    [Fact]
    public void Denies_when_any_resolved_address_is_internal()
    {
        var result = Policy.Check("https://example.com/", Resolve("93.184.216.34", "127.0.0.1"));
        Assert.False(result.Allowed);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    public void IsPublic_classifies_addresses(string ip, bool expected)
        => Assert.Equal(expected, UrlSecurityPolicy.IsPublic(IPAddress.Parse(ip)));
}
