namespace Contoso.Payments.Application.Abstractions;

public interface IWebhookSignatureVerifier
{
    /// <summary>
    /// Verify an HMAC-SHA256 signature applied to the raw request body.  <paramref name="signatureHeader"/>
    /// is expected in the format <c>t=&lt;unixSeconds&gt;,v1=&lt;hex&gt;</c>.  Returns false on any error
    /// (missing parts, malformed hex, expired timestamp, or signature mismatch) — never throws.
    /// </summary>
    bool Verify(string signatureHeader, ReadOnlySpan<byte> rawBody, DateTimeOffset now, out string? failureReason);

    /// <summary>Compute a signature (for demo/test scripts).</summary>
    string Sign(long unixSeconds, ReadOnlySpan<byte> rawBody);
}
