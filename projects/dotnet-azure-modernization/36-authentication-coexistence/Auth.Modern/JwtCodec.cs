using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Auth.Modern;

public enum JwtAlgorithm
{
    Hs256,
    Rs256,
}

public enum JwtFailure
{
    None,
    Malformed,
    BadSignature,
    UnexpectedAlgorithm,
    Expired,
    NotYetValid,
    WrongIssuer,
    WrongAudience,
    NonceMismatch,
}

public sealed record JwtValidation(JsonObject? Claims, JwtFailure Failure)
{
    public bool Ok => Failure == JwtFailure.None;

    public string? Subject => Claims?["sub"]?.GetValue<string>();
}

/// <summary>
/// A JWT signer and validator whose algorithm is fixed at construction.
/// </summary>
/// <remarks>
/// <para>
/// The single most important line in this class is that <see cref="JwtAlgorithm"/> is a
/// constructor parameter and the <c>alg</c> header is only ever compared against it, never
/// used to select a verifier. Reading the algorithm out of the token and dispatching on it
/// is the root of both classic JWT breaks: <c>alg: none</c>, where the attacker deletes the
/// signature, and algorithm confusion, where an RSA public key -- which is public -- is
/// presented to an HMAC verifier as the shared secret. Both are attacks on the dispatch,
/// not on the cryptography.
/// </para>
/// <para>
/// So there is no dispatch. The header is validated as data, like every other attacker
/// controlled field.
/// </para>
/// </remarks>
public sealed class JwtCodec
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private readonly JwtAlgorithm _algorithm;
    private readonly byte[]? _secret;
    private readonly RSA? _rsa;

    public JwtCodec(byte[] hmacSecret)
    {
        _algorithm = JwtAlgorithm.Hs256;
        _secret = hmacSecret;
    }

    public JwtCodec(RSA rsa)
    {
        _algorithm = JwtAlgorithm.Rs256;
        _rsa = rsa;
    }

    public JwtAlgorithm Algorithm => _algorithm;

    public string Encode(JsonObject claims)
    {
        var header = new JsonObject
        {
            ["alg"] = _algorithm == JwtAlgorithm.Hs256 ? "HS256" : "RS256",
            ["typ"] = "JWT",
        };

        var signingInput =
            $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(header, Compact))}." +
            $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims, Compact))}";

        var signature = Sign(Encoding.ASCII.GetBytes(signingInput));
        return $"{signingInput}.{Base64Url(signature)}";
    }

    public JwtValidation Validate(
        string token,
        string expectedIssuer,
        string expectedAudience,
        DateTimeOffset now,
        string? expectedNonce = null)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return new JwtValidation(null, JwtFailure.Malformed);

        JsonObject header;
        JsonObject claims;
        byte[] signature;
        try
        {
            header = JsonNode.Parse(FromBase64Url(parts[0]))?.AsObject()
                     ?? throw new JsonException("null header");
            claims = JsonNode.Parse(FromBase64Url(parts[1]))?.AsObject()
                     ?? throw new JsonException("null payload");
            signature = FromBase64Url(parts[2]);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return new JwtValidation(null, JwtFailure.Malformed);
        }

        var declared = header["alg"]?.GetValue<string>();
        var required = _algorithm == JwtAlgorithm.Hs256 ? "HS256" : "RS256";
        if (declared != required) return new JwtValidation(null, JwtFailure.UnexpectedAlgorithm);

        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        if (!Verify(signingInput, signature)) return new JwtValidation(null, JwtFailure.BadSignature);

        if (claims["iss"]?.GetValue<string>() != expectedIssuer)
        {
            return new JwtValidation(null, JwtFailure.WrongIssuer);
        }

        if (!AudienceMatches(claims["aud"], expectedAudience))
        {
            return new JwtValidation(null, JwtFailure.WrongAudience);
        }

        var seconds = now.ToUnixTimeSeconds();
        if (claims["nbf"] is { } nbf && seconds < nbf.GetValue<long>())
        {
            return new JwtValidation(null, JwtFailure.NotYetValid);
        }

        if (claims["exp"] is { } exp && seconds >= exp.GetValue<long>())
        {
            return new JwtValidation(null, JwtFailure.Expired);
        }

        if (expectedNonce is not null && claims["nonce"]?.GetValue<string>() != expectedNonce)
        {
            return new JwtValidation(null, JwtFailure.NonceMismatch);
        }

        return new JwtValidation(claims, JwtFailure.None);
    }

    /// <summary><c>aud</c> is a string or an array of strings; both are legal per RFC 7519.</summary>
    private static bool AudienceMatches(JsonNode? audience, string expected) => audience switch
    {
        null => false,
        JsonArray array => array.Any(a => a?.GetValue<string>() == expected),
        _ => audience.GetValue<string>() == expected,
    };

    private byte[] Sign(byte[] input) => _algorithm switch
    {
        JwtAlgorithm.Hs256 => HMACSHA256.HashData(_secret!, input),
        JwtAlgorithm.Rs256 => _rsa!.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
        _ => throw new InvalidOperationException(),
    };

    private bool Verify(byte[] input, byte[] signature) => _algorithm switch
    {
        JwtAlgorithm.Hs256 => CryptographicOperations.FixedTimeEquals(
            HMACSHA256.HashData(_secret!, input), signature),
        JwtAlgorithm.Rs256 => _rsa!.VerifyData(
            input, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
        _ => throw new InvalidOperationException(),
    };

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
}
