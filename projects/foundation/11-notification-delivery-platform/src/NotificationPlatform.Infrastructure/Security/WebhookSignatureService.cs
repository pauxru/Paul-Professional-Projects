namespace NotificationPlatform.Infrastructure.Security;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NotificationPlatform.Application.Abstractions;
using NotificationPlatform.Application.Options;

/// <summary>
/// HMAC-SHA256 signature: hex(hmac(key, timestamp + "." + body)).
/// Constant-time verification.  Callers supply timestamp/nonce headers and enforce replay via
/// a persisted nonce store.
/// </summary>
public sealed class WebhookSignatureService : IWebhookSignatureService
{
    private readonly WebhookOptions _options;

    public WebhookSignatureService(IOptions<WebhookOptions> options)
    {
        _options = options.Value;
    }

    public string Sign(string body, DateTimeOffset now)
    {
        var ts = now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var input = ts + "." + body;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.SigningKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant() + "." + ts;
    }

    public SignatureValidationResult Verify(string signatureHeader, string timestampHeader, string body, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader)) return new SignatureValidationResult(false, "missing_signature");
        if (string.IsNullOrWhiteSpace(timestampHeader)) return new SignatureValidationResult(false, "missing_timestamp");
        if (!long.TryParse(timestampHeader, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var unix))
            return new SignatureValidationResult(false, "bad_timestamp");
        var ts = DateTimeOffset.FromUnixTimeSeconds(unix);
        if ((now - ts).Duration() > tolerance)
            return new SignatureValidationResult(false, "expired_timestamp");

        var expected = ComputeHex(timestampHeader + "." + body);
        var provided = signatureHeader.Split('.', StringSplitOptions.RemoveEmptyEntries)[0];
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var providedBytes = Encoding.ASCII.GetBytes(provided);
        if (expectedBytes.Length != providedBytes.Length)
            return new SignatureValidationResult(false, "signature_mismatch");
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes)
            ? new SignatureValidationResult(true, null)
            : new SignatureValidationResult(false, "signature_mismatch");
    }

    private string ComputeHex(string input)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.SigningKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
