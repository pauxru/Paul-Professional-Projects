using System.Text;

using Microsoft.Extensions.Options;

using Contoso.Payments.Application.Common;
using Contoso.Payments.Infrastructure.Webhooks;

namespace Contoso.Payments.UnitTests.Domain;

public class HmacSha256WebhookSignatureVerifierTests
{
    private static readonly PaymentProviderOptions Opts = new()
    {
        WebhookSigningSecret = "supersecret-that-is-at-least-32-chars-long!",
        WebhookTimestampToleranceSeconds = 300
    };

    private static HmacSha256WebhookSignatureVerifier NewVerifier()
    {
        var monitor = new TestMonitor(Opts);
        return new HmacSha256WebhookSignatureVerifier(monitor);
    }

    [Fact]
    public void Verify_returns_true_for_valid_signature()
    {
        var v = NewVerifier();
        var now = DateTimeOffset.UtcNow;
        var body = Encoding.UTF8.GetBytes("{\"a\":1}");
        var sig = v.Sign(now.ToUnixTimeSeconds(), body);
        var header = $"t={now.ToUnixTimeSeconds()},v1={sig}";
        Assert.True(v.Verify(header, body, now, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void Verify_returns_false_for_tampered_body()
    {
        var v = NewVerifier();
        var now = DateTimeOffset.UtcNow;
        var body = Encoding.UTF8.GetBytes("{\"a\":1}");
        var sig = v.Sign(now.ToUnixTimeSeconds(), body);
        var header = $"t={now.ToUnixTimeSeconds()},v1={sig}";
        var tampered = Encoding.UTF8.GetBytes("{\"a\":2}");
        Assert.False(v.Verify(header, tampered, now, out var reason));
        Assert.Equal("signature mismatch", reason);
    }

    [Fact]
    public void Verify_returns_false_for_expired_timestamp()
    {
        var v = NewVerifier();
        var now = DateTimeOffset.UtcNow;
        var stale = now.AddSeconds(-Opts.WebhookTimestampToleranceSeconds - 10);
        var body = Encoding.UTF8.GetBytes("{\"a\":1}");
        var sig = v.Sign(stale.ToUnixTimeSeconds(), body);
        var header = $"t={stale.ToUnixTimeSeconds()},v1={sig}";
        Assert.False(v.Verify(header, body, now, out var reason));
        Assert.Equal("timestamp outside tolerance", reason);
    }

    [Fact]
    public void Verify_returns_false_for_missing_header()
    {
        var v = NewVerifier();
        Assert.False(v.Verify("", Array.Empty<byte>(), DateTimeOffset.UtcNow, out var reason));
        Assert.Equal("missing signature header", reason);
    }

    [Fact]
    public void Verify_returns_false_for_malformed_header()
    {
        var v = NewVerifier();
        Assert.False(v.Verify("garbage", Array.Empty<byte>(), DateTimeOffset.UtcNow, out var reason));
        Assert.Equal("malformed signature", reason);
    }

    [Fact]
    public void Verify_returns_false_for_wrong_secret()
    {
        var v1 = NewVerifier();
        var otherOpts = new PaymentProviderOptions
        {
            WebhookSigningSecret = "different-secret-that-is-also-32-chars-!",
            WebhookTimestampToleranceSeconds = 300
        };
        var v2 = new HmacSha256WebhookSignatureVerifier(new TestMonitor(otherOpts));
        var now = DateTimeOffset.UtcNow;
        var body = Encoding.UTF8.GetBytes("{\"a\":1}");
        var sig = v2.Sign(now.ToUnixTimeSeconds(), body);
        var header = $"t={now.ToUnixTimeSeconds()},v1={sig}";
        Assert.False(v1.Verify(header, body, now, out var reason));
        Assert.Equal("signature mismatch", reason);
    }

    private sealed class TestMonitor : IOptionsMonitor<PaymentProviderOptions>
    {
        public TestMonitor(PaymentProviderOptions value) { CurrentValue = value; }
        public PaymentProviderOptions CurrentValue { get; }
        public PaymentProviderOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PaymentProviderOptions, string?> listener) => null;
    }
}
