using System.Text;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Infrastructure.Security;
using ZeroTrust.Infrastructure.Time;

namespace ZeroTrust.UnitTests.Security;

public class SecretHasherTests
{
    [Fact]
    public void Hash_Then_Verify_Succeeds()
    {
        var (hash, salt) = SecretHasher.Hash("hunter2");
        Assert.True(SecretHasher.Verify("hunter2", hash, salt));
    }

    [Fact]
    public void Verify_Rejects_Wrong_Password()
    {
        var (hash, salt) = SecretHasher.Hash("hunter2");
        Assert.False(SecretHasher.Verify("wrong", hash, salt));
    }

    [Fact]
    public void Same_Password_Different_Salt_Yields_Different_Hash()
    {
        var a = SecretHasher.Hash("hunter2");
        var b = SecretHasher.Hash("hunter2");
        Assert.NotEqual(a.hash, b.hash);
    }
}

public class HmacWebhookTests
{
    private const string Secret = "test-webhook-secret";
    private static readonly IWebhookSigner Signer = new HmacWebhookSigner();
    private static readonly IWebhookVerifier Verifier = new HmacWebhookVerifier();

    [Fact]
    public void Valid_Signature_Passes()
    {
        HmacWebhookVerifier.ClearReplayCacheForTests();
        var now = DateTime.UtcNow;
        var ts = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds();
        var nonce = Guid.NewGuid().ToString("N");
        var body = "{\"event\":\"payment.completed\"}";
        var sig = Signer.Sign(body, ts, nonce, Secret);
        var res = Verifier.Verify(body, sig, ts.ToString(), nonce, Secret, now, TimeSpan.FromMinutes(5));
        Assert.True(res.IsValid);
    }

    [Fact]
    public void Tampered_Body_Rejected()
    {
        HmacWebhookVerifier.ClearReplayCacheForTests();
        var now = DateTime.UtcNow;
        var ts = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds();
        var nonce = Guid.NewGuid().ToString("N");
        var sig = Signer.Sign("{\"a\":1}", ts, nonce, Secret);
        var res = Verifier.Verify("{\"a\":2}", sig, ts.ToString(), nonce, Secret, now, TimeSpan.FromMinutes(5));
        Assert.False(res.IsValid);
    }

    [Fact]
    public void Expired_Signature_Rejected()
    {
        HmacWebhookVerifier.ClearReplayCacheForTests();
        var now = DateTime.UtcNow;
        var oldTs = new DateTimeOffset(now.AddMinutes(-10), TimeSpan.Zero).ToUnixTimeSeconds();
        var nonce = Guid.NewGuid().ToString("N");
        var body = "{\"x\":1}";
        var sig = Signer.Sign(body, oldTs, nonce, Secret);
        var res = Verifier.Verify(body, sig, oldTs.ToString(), nonce, Secret, now, TimeSpan.FromMinutes(5));
        Assert.False(res.IsValid);
        Assert.Equal("timestamp_outside_tolerance", res.Reason);
    }

    [Fact]
    public void Replayed_Nonce_Rejected()
    {
        HmacWebhookVerifier.ClearReplayCacheForTests();
        var now = DateTime.UtcNow;
        var ts = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds();
        var nonce = "same-nonce-" + Guid.NewGuid().ToString("N");
        var body = "{\"x\":1}";
        var sig = Signer.Sign(body, ts, nonce, Secret);
        var first = Verifier.Verify(body, sig, ts.ToString(), nonce, Secret, now, TimeSpan.FromMinutes(5));
        var second = Verifier.Verify(body, sig, ts.ToString(), nonce, Secret, now, TimeSpan.FromMinutes(5));
        Assert.True(first.IsValid);
        Assert.False(second.IsValid);
        Assert.Equal("replayed_nonce", second.Reason);
    }

    [Fact]
    public void Bad_Signature_Format_Rejected()
    {
        HmacWebhookVerifier.ClearReplayCacheForTests();
        var res = Verifier.Verify("body", "no-equals-sign", "1", "n", Secret, DateTime.UtcNow, TimeSpan.FromMinutes(5));
        Assert.False(res.IsValid);
    }

    [Fact]
    public void Signature_With_Wrong_Version_Rejected()
    {
        HmacWebhookVerifier.ClearReplayCacheForTests();
        var now = DateTime.UtcNow;
        var ts = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds();
        var nonce = Guid.NewGuid().ToString("N");
        var body = "x";
        var goodSig = Signer.Sign(body, ts, nonce, Secret);
        var tampered = "v9=" + goodSig[3..];
        var res = Verifier.Verify(body, tampered, ts.ToString(), nonce, Secret, now, TimeSpan.FromMinutes(5));
        Assert.False(res.IsValid);
        Assert.Equal("unsupported_scheme_version", res.Reason);
    }
}
