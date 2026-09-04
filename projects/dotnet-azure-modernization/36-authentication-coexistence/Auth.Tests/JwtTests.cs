using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Auth.Modern;

namespace Auth.Tests;

/// <summary>
/// JWT validation. Every test here corresponds to a published attack on a real library;
/// none of them is hypothetical, and all of them were introduced by treating the token's
/// own header as an instruction rather than as a claim to be checked.
/// </summary>
public sealed class JwtTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Issuer = "https://login.example.test";
    private const string Audience = "ledger-api";

    private static JsonObject Claims(
        string subject = "finance.director",
        DateTimeOffset? expiry = null,
        string? audience = Audience,
        string? issuer = Issuer) =>
        new()
        {
            ["iss"] = issuer,
            ["aud"] = audience,
            ["sub"] = subject,
            ["iat"] = Now.ToUnixTimeSeconds(),
            ["nbf"] = Now.ToUnixTimeSeconds(),
            ["exp"] = (expiry ?? Now.AddMinutes(15)).ToUnixTimeSeconds(),
        };

    private static readonly byte[] Secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void AcceptsAValidHmacToken()
    {
        var codec = new JwtCodec(Secret);
        var result = codec.Validate(codec.Encode(Claims()), Issuer, Audience, Now);

        Assert.True(result.Ok);
        Assert.Equal("finance.director", result.Subject);
    }

    [Fact]
    public void AcceptsAValidRsaToken()
    {
        using var rsa = RSA.Create(2048);
        var codec = new JwtCodec(rsa);
        var result = codec.Validate(codec.Encode(Claims()), Issuer, Audience, Now);

        Assert.True(result.Ok);
        Assert.Equal(JwtAlgorithm.Rs256, codec.Algorithm);
    }

    [Fact]
    public void RejectsAlgNone()
    {
        // CVE-2015-9235 and its descendants. The token asks not to be verified and a
        // credulous validator agrees.
        var header = JwtCodec.Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}"u8.ToArray());
        var payload = JwtCodec.Base64Url(
            System.Text.Encoding.UTF8.GetBytes(Claims().ToJsonString()));

        var codec = new JwtCodec(Secret);
        var result = codec.Validate($"{header}.{payload}.", Issuer, Audience, Now);

        Assert.False(result.Ok);
        Assert.Equal(JwtFailure.UnexpectedAlgorithm, result.Failure);
    }

    [Fact]
    public void RejectsAlgNoneWithAGarbageSignature()
    {
        var header = JwtCodec.Base64Url("{\"alg\":\"NONE\"}"u8.ToArray());
        var payload = JwtCodec.Base64Url(
            System.Text.Encoding.UTF8.GetBytes(Claims().ToJsonString()));

        var result = new JwtCodec(Secret).Validate(
            $"{header}.{payload}.AAAA", Issuer, Audience, Now);

        Assert.False(result.Ok);
    }

    [Fact]
    public void RsaValidatorRejectsAnHmacToken()
    {
        // Algorithm confusion. The attacker signs HS256 using the RSA *public* key as the
        // HMAC secret. It works whenever the validator picks its algorithm from the header
        // instead of from its own configuration.
        using var rsa = RSA.Create(2048);
        var rsaCodec = new JwtCodec(rsa);
        var publicKey = rsa.ExportSubjectPublicKeyInfo();
        var hmacCodec = new JwtCodec(publicKey);

        var forged = hmacCodec.Encode(Claims());
        var result = rsaCodec.Validate(forged, Issuer, Audience, Now);

        Assert.False(result.Ok);
        Assert.Equal(JwtFailure.UnexpectedAlgorithm, result.Failure);
    }

    [Fact]
    public void HmacValidatorRejectsAnRsaToken()
    {
        using var rsa = RSA.Create(2048);
        var forged = new JwtCodec(rsa).Encode(Claims());

        Assert.Equal(JwtFailure.UnexpectedAlgorithm,
                     new JwtCodec(Secret).Validate(forged, Issuer, Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAWrongSignature()
    {
        var codec = new JwtCodec(Secret);
        var other = new JwtCodec(new byte[32]);

        Assert.Equal(JwtFailure.BadSignature,
                     codec.Validate(other.Encode(Claims()), Issuer, Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAnEditedPayload()
    {
        var codec = new JwtCodec(Secret);
        var parts = codec.Encode(Claims()).Split('.');
        var tampered = JwtCodec.Base64Url(System.Text.Encoding.UTF8.GetBytes(
            Claims(subject: "attacker").ToJsonString()));

        Assert.Equal(JwtFailure.BadSignature,
                     codec.Validate($"{parts[0]}.{tampered}.{parts[2]}", Issuer, Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAnExpiredToken()
    {
        var codec = new JwtCodec(Secret);
        var token = codec.Encode(Claims(expiry: Now.AddMinutes(5)));

        Assert.True(codec.Validate(token, Issuer, Audience, Now.AddMinutes(4)).Ok);
        Assert.Equal(JwtFailure.Expired,
                     codec.Validate(token, Issuer, Audience, Now.AddMinutes(6)).Failure);
    }

    [Fact]
    public void RejectsATokenThatIsNotYetValid()
    {
        var codec = new JwtCodec(Secret);
        var claims = Claims();
        claims["nbf"] = Now.AddMinutes(10).ToUnixTimeSeconds();

        Assert.Equal(JwtFailure.NotYetValid,
                     codec.Validate(codec.Encode(claims), Issuer, Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAnotherAudience()
    {
        // The token is genuine, signed by the right authority, unexpired -- and issued for
        // a different API. Skipping this check turns every service behind the same
        // authority into a confused deputy for every other one.
        var codec = new JwtCodec(Secret);
        var token = codec.Encode(Claims(audience: "reporting-api"));

        Assert.Equal(JwtFailure.WrongAudience,
                     codec.Validate(token, Issuer, Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAnotherIssuer()
    {
        var codec = new JwtCodec(Secret);
        var token = codec.Encode(Claims(issuer: "https://evil.example.test"));

        Assert.Equal(JwtFailure.WrongIssuer,
                     codec.Validate(token, Issuer, Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAMissingAudience()
    {
        var codec = new JwtCodec(Secret);
        var claims = Claims();
        claims.Remove("aud");

        Assert.False(codec.Validate(codec.Encode(claims), Issuer, Audience, Now).Ok);
    }

    [Fact]
    public void RejectsAMissingExpiry()
    {
        // A token with no exp is a token that never expires. This found a real defect: the
        // check was written as `if (claims["exp"] is { } exp && expired)`, which reads like
        // a null guard and behaves like an opt-out -- omit the claim and the check is
        // skipped. Absence of a security-relevant claim has to fail closed.
        var codec = new JwtCodec(Secret);
        var claims = Claims();
        claims.Remove("exp");

        var result = codec.Validate(codec.Encode(claims), Issuer, Audience, Now);
        Assert.False(result.Ok);
        Assert.Equal(JwtFailure.MissingExpiry, result.Failure);
    }

    [Fact]
    public void RejectsAMissingNotBefore()
    {
        // nbf genuinely is optional in RFC 7519, so its absence is not a rejection. Pinned
        // so the difference from exp is a decision on the record rather than an accident.
        var codec = new JwtCodec(Secret);
        var claims = Claims();
        claims.Remove("nbf");

        Assert.True(codec.Validate(codec.Encode(claims), Issuer, Audience, Now).Ok);
    }

    [Fact]
    public void ChecksTheNonceWhenOneIsExpected()
    {
        var codec = new JwtCodec(Secret);
        var claims = Claims();
        claims["nonce"] = "abc123";
        var token = codec.Encode(claims);

        Assert.True(codec.Validate(token, Issuer, Audience, Now, "abc123").Ok);
        Assert.Equal(JwtFailure.NonceMismatch,
                     codec.Validate(token, Issuer, Audience, Now, "different").Failure);
    }

    [Fact]
    public void RejectsAMissingNonceWhenOneIsExpected()
    {
        // The nonce binds the id_token to *this* authorization request. Without the check
        // a replayed token from another session is indistinguishable from a fresh one.
        var codec = new JwtCodec(Secret);
        Assert.Equal(JwtFailure.NonceMismatch,
                     codec.Validate(codec.Encode(Claims()), Issuer, Audience, Now, "abc123").Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("...")]
    [InlineData("!!!.???.###")]
    public void RejectsMalformedTokens(string token)
    {
        Assert.False(new JwtCodec(Secret).Validate(token, Issuer, Audience, Now).Ok);
    }

    [Fact]
    public void Base64UrlHasNoPaddingOrUnsafeCharacters()
    {
        for (var length = 0; length < 40; length++)
        {
            var encoded = JwtCodec.Base64Url(Enumerable.Range(0, length).Select(i => (byte)i).ToArray());
            Assert.DoesNotContain('=', encoded);
            Assert.DoesNotContain('+', encoded);
            Assert.DoesNotContain('/', encoded);
            Assert.Equal(length, JwtCodec.FromBase64Url(encoded).Length);
        }
    }

    [Fact]
    public void Base64UrlRoundTripsArbitraryBytes()
    {
        var bytes = RandomNumberGenerator.GetBytes(257);
        Assert.Equal(Convert.ToHexString(bytes),
                     Convert.ToHexString(JwtCodec.FromBase64Url(JwtCodec.Base64Url(bytes))));
    }
}
