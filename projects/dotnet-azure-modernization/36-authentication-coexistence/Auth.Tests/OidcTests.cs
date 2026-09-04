using System.Security.Cryptography;
using Auth.Modern;

namespace Auth.Tests;

/// <summary>
/// The authorization code flow with PKCE. Every check in here exists because the flow
/// without it was deployed at scale first.
/// </summary>
public sealed class OidcTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Issuer = "https://login.example.test";
    private const string ClientId = "ledger-web";
    private const string Redirect = "https://ledger.example.test/signin-oidc";

    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoClaims = [];

    private static OidcAuthority Authority(RSA rsa, bool requirePkce = true) =>
        new(new JwtCodec(rsa), Issuer,
            [new OidcClient(ClientId, [Redirect], requirePkce)]);

    private static (OidcAuthority Authority, RSA Key) Fresh(bool requirePkce = true)
    {
        var rsa = RSA.Create(2048);
        return (Authority(rsa, requirePkce), rsa);
    }

    [Fact]
    public void CompletesTheHappyPath()
    {
        var (authority, rsa) = Fresh();
        using var _ = rsa;

        var verifier = OidcAuthority.CreateVerifier();
        var (code, failure) = authority.Authorize(
            ClientId, Redirect, "nonce-1", OidcAuthority.Challenge(verifier), "S256",
            "finance.director", NoClaims, Now);

        Assert.Equal(OidcFailure.None, failure);
        Assert.NotNull(code);

        var result = authority.Redeem(code!, ClientId, Redirect, verifier, Now);
        Assert.True(result.Ok);

        var validation = new JwtCodec(rsa).Validate(
            result.Tokens!.IdToken, Issuer, ClientId, Now, "nonce-1");
        Assert.True(validation.Ok);
        Assert.Equal("finance.director", validation.Subject);
    }

    [Fact]
    public void RejectsAnUnknownClient()
    {
        var (authority, rsa) = Fresh();
        using var _ = rsa;

        var (code, failure) = authority.Authorize(
            "not-registered", Redirect, "n", OidcAuthority.Challenge("v"), "S256",
            "u", NoClaims, Now);

        Assert.Null(code);
        Assert.Equal(OidcFailure.UnknownClient, failure);
    }

    [Theory]
    [InlineData("https://ledger.example.test/signin-oidc/")]        // trailing slash
    [InlineData("https://ledger.example.test/signin-oidc?x=1")]     // extra query
    [InlineData("https://ledger.example.test.evil.test/signin-oidc")]
    [InlineData("https://ledger.example.test/signin-oidc/../x")]
    [InlineData("http://ledger.example.test/signin-oidc")]          // scheme downgrade
    public void RejectsAnythingButAnExactRedirectMatch(string redirect)
    {
        // Prefix and suffix matching is how open redirectors become account takeovers: the
        // code is delivered to a URL the attacker controls and the flow completes normally
        // from everyone else's point of view.
        var (authority, rsa) = Fresh();
        using var _ = rsa;

        var (code, failure) = authority.Authorize(
            ClientId, redirect, "n", OidcAuthority.Challenge("v"), "S256", "u", NoClaims, Now);

        Assert.Null(code);
        Assert.Equal(OidcFailure.RedirectUriMismatch, failure);
    }

    [Fact]
    public void RefusesThePlainChallengeMethod()
    {
        // "plain" sends the verifier itself as the challenge, so anyone who can read the
        // authorization request can complete the exchange. It is in the specification for
        // compatibility with clients that cannot compute SHA-256; on a server written in
        // 2026 it is only an attacker's preference.
        var (authority, rsa) = Fresh();
        using var _ = rsa;

        var (code, failure) = authority.Authorize(
            ClientId, Redirect, "n", "verifier-as-challenge", "plain", "u", NoClaims, Now);

        Assert.Null(code);
        Assert.Equal(OidcFailure.UnsupportedChallengeMethod, failure);
    }

    [Fact]
    public void RequiresAChallengeWhenTheClientRequiresPkce()
    {
        var (authority, rsa) = Fresh();
        using var _ = rsa;

        var (code, failure) = authority.Authorize(
            ClientId, Redirect, "n", "", "S256", "u", NoClaims, Now);

        Assert.Null(code);
        Assert.Equal(OidcFailure.PkceMissing, failure);
    }

    [Fact]
    public void RejectsTheWrongVerifier()
    {
        var (authority, rsa) = Fresh();
        using var _ = rsa;

        var verifier = OidcAuthority.CreateVerifier();
        var (code, _) = authority.Authorize(
            ClientId, Redirect, "n", OidcAuthority.Challenge(verifier), "S256",
            "u", NoClaims, Now);

        var result = authority.Redeem(code!, ClientId, Redirect, OidcAuthority.CreateVerifier(), Now);

        Assert.False(result.Ok);
        Assert.Equal(OidcFailure.PkceMismatch, result.Failure);
    }

    [Fact]
    public void RejectsAReplayedCode()
    {
        // A leaked code is a leaked session. The specification says to revoke everything
        // already issued from it; the minimum is that the second redemption fails.
        var (authority, rsa) = Fresh();
        using var _ = rsa;

        var verifier = OidcAuthority.CreateVerifier();
        var (code, _) = authority.Authorize(
            ClientId, Redirect, "n", OidcAuthority.Challenge(verifier), "S256",
            "u", NoClaims, Now);

        Assert.True(authority.Redeem(code!, ClientId, Redirect, verifier, Now).Ok);

        var second = authority.Redeem(code!, ClientId, Redirect, verifier, Now);
        Assert.False(second.Ok);
        Assert.Equal(OidcFailure.CodeAlreadyRedeemed, second.Failure);
    }

    [Fact]
    public void RejectsAnExpiredCode()
    {
        var (authority, rsa) = Fresh();
        using var _ = rsa;

        var verifier = OidcAuthority.CreateVerifier();
        var (code, _) = authority.Authorize(
            ClientId, Redirect, "n", OidcAuthority.Challenge(verifier), "S256",
            "u", NoClaims, Now);

        var result = authority.Redeem(code!, ClientId, Redirect, verifier, Now.AddMinutes(30));
        Assert.Equal(OidcFailure.CodeExpired, result.Failure);
    }

    [Fact]
    public void RejectsAnUnknownCode()
    {
        var (authority, rsa) = Fresh();
        using var _ = rsa;

        Assert.Equal(OidcFailure.UnknownCode,
                     authority.Redeem("made-up", ClientId, Redirect, "v", Now).Failure);
    }

    [Fact]
    public void RejectsRedemptionByADifferentClient()
    {
        var rsa = RSA.Create(2048);
        using var _ = rsa;
        var authority = new OidcAuthority(new JwtCodec(rsa), Issuer,
        [
            new OidcClient(ClientId, [Redirect]),
            new OidcClient("other-app", ["https://other.example.test/cb"]),
        ]);

        var verifier = OidcAuthority.CreateVerifier();
        var (code, _) = authority.Authorize(
            ClientId, Redirect, "n", OidcAuthority.Challenge(verifier), "S256",
            "u", NoClaims, Now);

        Assert.False(authority.Redeem(code!, "other-app", Redirect, verifier, Now).Ok);
    }

    [Fact]
    public void RejectsRedemptionWithADifferentRedirectUri()
    {
        var (authority, rsa) = Fresh();
        using var _ = rsa;

        var verifier = OidcAuthority.CreateVerifier();
        var (code, _) = authority.Authorize(
            ClientId, Redirect, "n", OidcAuthority.Challenge(verifier), "S256",
            "u", NoClaims, Now);

        Assert.Equal(OidcFailure.RedirectUriMismatch,
                     authority.Redeem(code!, ClientId, "https://elsewhere.test/cb", verifier, Now)
                              .Failure);
    }

    [Fact]
    public void OutstandingCodesFallToZeroAfterRedemption()
    {
        var (authority, rsa) = Fresh();
        using var _ = rsa;

        Assert.Equal(0, authority.OutstandingCodes);

        var verifier = OidcAuthority.CreateVerifier();
        var (code, _) = authority.Authorize(
            ClientId, Redirect, "n", OidcAuthority.Challenge(verifier), "S256",
            "u", NoClaims, Now);

        Assert.Equal(1, authority.OutstandingCodes);
        authority.Redeem(code!, ClientId, Redirect, verifier, Now);
        Assert.Equal(0, authority.OutstandingCodes);
    }

    [Fact]
    public void CodesAreNotGuessable()
    {
        var (authority, rsa) = Fresh();
        using var _ = rsa;

        var codes = new HashSet<string>();
        for (var i = 0; i < 50; i++)
        {
            var verifier = OidcAuthority.CreateVerifier();
            var (code, _) = authority.Authorize(
                ClientId, Redirect, "n", OidcAuthority.Challenge(verifier), "S256",
                "u", NoClaims, Now);
            codes.Add(code!);
        }

        Assert.Equal(50, codes.Count);
        Assert.All(codes, c => Assert.True(c.Length >= 32));
    }

    [Fact]
    public void ChallengeIsTheUrlSafeSha256OfTheVerifier()
    {
        var verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var expected = JwtCodec.Base64Url(
            SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));

        Assert.Equal(expected, OidcAuthority.Challenge(verifier));

        // RFC 7636 Appendix B pins this exact pair.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
                     OidcAuthority.Challenge(verifier));
    }

    [Fact]
    public void VerifiersAreDistinct()
    {
        var verifiers = Enumerable.Range(0, 50)
                                  .Select(_ => OidcAuthority.CreateVerifier())
                                  .ToHashSet();
        Assert.Equal(50, verifiers.Count);
    }

    [Fact]
    public void IdTokenCarriesTheRequestedClaims()
    {
        var (authority, rsa) = Fresh();
        using var _ = rsa;

        var verifier = OidcAuthority.CreateVerifier();
        var (code, _) = authority.Authorize(
            ClientId, Redirect, "n", OidcAuthority.Challenge(verifier), "S256",
            "finance.director",
            [new KeyValuePair<string, string>("tenant", "north"),
             new KeyValuePair<string, string>("roles", "Admin")],
            Now);

        var tokens = authority.Redeem(code!, ClientId, Redirect, verifier, Now).Tokens!;
        var claims = new JwtCodec(rsa).Validate(tokens.IdToken, Issuer, ClientId, Now).Claims!;

        Assert.Equal("north", claims["tenant"]!.GetValue<string>());
        Assert.Equal("Admin", claims["roles"]!.GetValue<string>());
    }

    [Fact]
    public void APkceExemptClientStillWorks()
    {
        // Some machine-to-machine clients genuinely cannot hold a verifier. Making the
        // exemption per-client and explicit is the difference between a documented
        // exception and a global weakening nobody remembers agreeing to.
        var (authority, rsa) = Fresh(requirePkce: false);
        using var _ = rsa;

        var (code, failure) = authority.Authorize(
            ClientId, Redirect, "n", "", "S256", "u", NoClaims, Now);

        Assert.Equal(OidcFailure.None, failure);
        Assert.True(authority.Redeem(code!, ClientId, Redirect, "", Now).Ok);
    }
}
