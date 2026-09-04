using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ZeroTrust.Application.Abstractions;

namespace ZeroTrust.Infrastructure.Security;

public sealed class HmacWebhookSigner : IWebhookSigner
{
    public string SchemeVersion => "v1";

    public string Sign(string rawBody, long unixTimestamp, string nonce, string secret)
    {
        var payload = $"{SchemeVersion}.{unixTimestamp}.{nonce}.{rawBody}";
        using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = mac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return $"{SchemeVersion}={Convert.ToHexString(sig).ToLowerInvariant()}";
    }
}

public sealed class HmacWebhookVerifier : IWebhookVerifier
{
    private static readonly ConcurrentDictionary<string, DateTime> _seenNonces = new();

    public WebhookVerificationResult Verify(string rawBody, string signatureHeader, string? timestampHeader,
        string? nonceHeader, string secret, DateTime nowUtc, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader)) return new(false, "missing_signature");
        if (string.IsNullOrWhiteSpace(timestampHeader) || !long.TryParse(timestampHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts))
            return new(false, "missing_timestamp");
        if (string.IsNullOrWhiteSpace(nonceHeader)) return new(false, "missing_nonce");

        var eqIdx = signatureHeader.IndexOf('=');
        if (eqIdx <= 0) return new(false, "bad_signature_format");
        var scheme = signatureHeader[..eqIdx];
        var hex = signatureHeader[(eqIdx + 1)..];
        if (scheme != "v1") return new(false, "unsupported_scheme_version");

        var requestTime = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
        if (Math.Abs((nowUtc - requestTime).TotalSeconds) > tolerance.TotalSeconds)
            return new(false, "timestamp_outside_tolerance");

        // Replay window: prune expired nonces then check.
        foreach (var kv in _seenNonces)
            if ((nowUtc - kv.Value) > tolerance)
                _seenNonces.TryRemove(kv.Key, out _);
        if (!_seenNonces.TryAdd(nonceHeader, nowUtc))
            return new(false, "replayed_nonce");

        var payload = $"{scheme}.{ts}.{nonceHeader}.{rawBody}";
        using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = mac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        byte[] provided;
        try { provided = Convert.FromHexString(hex); } catch { return new(false, "bad_signature_hex"); }
        if (provided.Length != expected.Length) return new(false, "signature_length_mismatch");
        if (!CryptographicOperations.FixedTimeEquals(expected, provided))
            return new(false, "signature_mismatch");
        return new(true, null);
    }

    // Test helper — clear the replay cache between tests.
    public static void ClearReplayCacheForTests() => _seenNonces.Clear();
}
