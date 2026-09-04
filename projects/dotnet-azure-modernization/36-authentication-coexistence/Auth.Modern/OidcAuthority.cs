using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Auth.Modern;

public enum OidcFailure
{
    None,
    UnknownClient,
    RedirectUriMismatch,
    UnknownCode,
    CodeAlreadyRedeemed,
    CodeExpired,
    PkceMissing,
    PkceMismatch,
    UnsupportedChallengeMethod,
}

public sealed record OidcClient(
    string ClientId,
    IReadOnlyList<string> RedirectUris,
    bool RequirePkce = true);

public sealed record TokenResponse(string IdToken, string AccessToken, int ExpiresInSeconds);

public sealed record TokenResult(TokenResponse? Tokens, OidcFailure Failure)
{
    public bool Ok => Failure == OidcFailure.None;
}

internal sealed record PendingCode(
    string ClientId,
    string Subject,
    string RedirectUri,
    string Nonce,
    string CodeChallenge,
    IReadOnlyList<KeyValuePair<string, string>> Claims,
    DateTimeOffset ExpiresAt)
{
    public bool Redeemed { get; set; }
}

/// <summary>
/// An OpenID Connect authorization server: authorization code flow with PKCE.
/// </summary>
/// <remarks>
/// <para>
/// Scoped to what the migration actually needs -- issue an id_token for a user the legacy
/// stack just authenticated, and prove the token came back to the same client that asked
/// for it. That second half is PKCE, and it is required rather than optional here because
/// the alternative only works when you can guarantee the client secret stays secret, which
/// a desktop application, a mobile application, and a single-page application all cannot.
/// </para>
/// <para>
/// Two properties are enforced that are easy to leave out and expensive to leave out:
/// authorization codes are single use, and <c>plain</c> is not an accepted challenge method.
/// A <c>plain</c> challenge is the verifier itself, so an attacker who can see the
/// authorization request can complete the exchange -- it satisfies the letter of PKCE while
/// providing none of it.
/// </para>
/// </remarks>
public sealed class OidcAuthority
{
    private readonly JwtCodec _codec;
    private readonly string _issuer;
    private readonly Dictionary<string, OidcClient> _clients;
    private readonly Dictionary<string, PendingCode> _codes = [];
    private readonly Func<byte[]> _codeSource;

    public OidcAuthority(
        JwtCodec codec,
        string issuer,
        IEnumerable<OidcClient> clients,
        Func<byte[]>? codeSource = null)
    {
        _codec = codec;
        _issuer = issuer;
        _clients = clients.ToDictionary(c => c.ClientId);
        _codeSource = codeSource ?? (() => RandomNumberGenerator.GetBytes(32));
    }

    public int OutstandingCodes => _codes.Count(c => !c.Value.Redeemed);

    public (string? Code, OidcFailure Failure) Authorize(
        string clientId,
        string redirectUri,
        string nonce,
        string codeChallenge,
        string codeChallengeMethod,
        string subject,
        IReadOnlyList<KeyValuePair<string, string>> claims,
        DateTimeOffset now)
    {
        if (!_clients.TryGetValue(clientId, out var client))
        {
            return (null, OidcFailure.UnknownClient);
        }

        // Exact match against a registered value. Prefix or suffix matching is how open
        // redirectors become account takeovers: the code is delivered to the attacker's URL
        // and everything downstream behaves perfectly correctly.
        if (!client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
        {
            return (null, OidcFailure.RedirectUriMismatch);
        }

        if (client.RequirePkce)
        {
            if (string.IsNullOrEmpty(codeChallenge)) return (null, OidcFailure.PkceMissing);
            if (codeChallengeMethod != "S256") return (null, OidcFailure.UnsupportedChallengeMethod);
        }

        var code = JwtCodec.Base64Url(_codeSource());
        _codes[code] = new PendingCode(
            clientId, subject, redirectUri, nonce, codeChallenge, claims,
            now.AddSeconds(60));

        return (code, OidcFailure.None);
    }

    public TokenResult Redeem(
        string code, string clientId, string redirectUri, string codeVerifier, DateTimeOffset now)
    {
        if (!_codes.TryGetValue(code, out var pending)) return new TokenResult(null, OidcFailure.UnknownCode);

        if (pending.Redeemed)
        {
            // A second redemption means the code leaked. The specification says to revoke
            // the tokens already issued from it; at minimum it must not succeed twice.
            return new TokenResult(null, OidcFailure.CodeAlreadyRedeemed);
        }

        if (now >= pending.ExpiresAt) return new TokenResult(null, OidcFailure.CodeExpired);
        if (pending.ClientId != clientId) return new TokenResult(null, OidcFailure.UnknownClient);
        if (pending.RedirectUri != redirectUri) return new TokenResult(null, OidcFailure.RedirectUriMismatch);

        if (pending.CodeChallenge.Length > 0)
        {
            if (string.IsNullOrEmpty(codeVerifier)) return new TokenResult(null, OidcFailure.PkceMissing);

            var computed = JwtCodec.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(pending.CodeChallenge)))
            {
                return new TokenResult(null, OidcFailure.PkceMismatch);
            }
        }

        pending.Redeemed = true;

        var claims = new JsonObject
        {
            ["iss"] = _issuer,
            ["sub"] = pending.Subject,
            ["aud"] = pending.ClientId,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(15).ToUnixTimeSeconds(),
            ["nonce"] = pending.Nonce,
        };

        foreach (var group in pending.Claims.GroupBy(c => c.Key))
        {
            var values = group.Select(g => g.Value).ToArray();
            claims[group.Key] = values.Length == 1
                ? JsonValue.Create(values[0])
                : new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }

        var idToken = _codec.Encode(claims);

        var accessClaims = (JsonObject)claims.DeepClone();
        accessClaims.Remove("nonce");
        accessClaims["typ"] = "at+jwt";
        var accessToken = _codec.Encode(accessClaims);

        return new TokenResult(new TokenResponse(idToken, accessToken, 900), OidcFailure.None);
    }

    public static string CreateVerifier() => JwtCodec.Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) =>
        JwtCodec.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}
