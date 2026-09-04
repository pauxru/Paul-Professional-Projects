using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Common;

namespace Contoso.Payments.Infrastructure.Webhooks;

/// <summary>
/// HMAC-SHA256 verifier over the RAW request body.  Signature header format is
/// <c>t=&lt;unixSeconds&gt;,v1=&lt;lowerHex&gt;</c>.  Signature is constant-time compared;
/// timestamp older than the tolerance window is rejected.
/// </summary>
public sealed class HmacSha256WebhookSignatureVerifier : IWebhookSignatureVerifier
{
    private readonly IOptionsMonitor<PaymentProviderOptions> _options;

    public HmacSha256WebhookSignatureVerifier(IOptionsMonitor<PaymentProviderOptions> options)
    {
        _options = options;
    }

    public bool Verify(string signatureHeader, ReadOnlySpan<byte> rawBody, DateTimeOffset now, out string? failureReason)
    {
        failureReason = null;
        var opt = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            failureReason = "missing signature header";
            return false;
        }
        long timestamp = 0;
        string? providedHex = null;
        foreach (var part in signatureHeader.Split(','))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length != 2) continue;
            switch (kv[0])
            {
                case "t":
                    long.TryParse(kv[1], out timestamp);
                    break;
                case "v1":
                    providedHex = kv[1];
                    break;
            }
        }
        if (timestamp == 0 || providedHex is null)
        {
            failureReason = "malformed signature";
            return false;
        }

        var age = Math.Abs(now.ToUnixTimeSeconds() - timestamp);
        if (age > opt.WebhookTimestampToleranceSeconds)
        {
            failureReason = "timestamp outside tolerance";
            return false;
        }

        var expected = ComputeSignature(opt.WebhookSigningSecret, timestamp, rawBody);
        var providedBytes = FromHex(providedHex);
        var expectedBytes = FromHex(expected);
        if (providedBytes is null || expectedBytes is null)
        {
            failureReason = "malformed signature hex";
            return false;
        }
        if (!CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
        {
            failureReason = "signature mismatch";
            return false;
        }
        return true;
    }

    public string Sign(long unixSeconds, ReadOnlySpan<byte> rawBody)
        => ComputeSignature(_options.CurrentValue.WebhookSigningSecret, unixSeconds, rawBody);

    private static string ComputeSignature(string secret, long unixSeconds, ReadOnlySpan<byte> rawBody)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var prefix = Encoding.UTF8.GetBytes(unixSeconds.ToString() + ".");
        var buffer = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(buffer, 0);
        rawBody.CopyTo(buffer.AsSpan(prefix.Length));
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(key, buffer, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[]? FromHex(string hex)
    {
        if (hex.Length % 2 != 0) return null;
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }
}
