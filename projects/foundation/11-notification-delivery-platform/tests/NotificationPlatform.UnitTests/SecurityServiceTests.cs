namespace NotificationPlatform.UnitTests;

using Microsoft.Extensions.Options;
using NotificationPlatform.Application.Options;
using NotificationPlatform.Infrastructure.Security;
using Xunit;

public sealed class SecurityServiceTests
{
    private static UnsubscribeTokenService MakeTokenService(int lifetimeDays = 30)
    {
        var webhook = Options.Create(new WebhookOptions { SigningKey = new string('k', 40) });
        var notif = Options.Create(new NotificationOptions { UnsubscribeTokenLifetimeDays = lifetimeDays });
        return new UnsubscribeTokenService(webhook, notif);
    }

    private static WebhookSignatureService MakeWebhookService(string key = "webhook-secret-must-be-at-least-thirty-two-chars")
    {
        var opts = Options.Create(new WebhookOptions { SigningKey = key });
        return new WebhookSignatureService(opts);
    }

    [Fact]
    public void UnsubscribeTokenRoundTripsAndValidates()
    {
        var svc = MakeTokenService();
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var token = svc.Issue(t, r, "marketing", now, TimeSpan.FromDays(30));
        var result = svc.Validate(token, now);
        Assert.True(result.IsValid);
        Assert.Equal(t, result.TenantId);
        Assert.Equal(r, result.RecipientId);
        Assert.Equal("marketing", result.Category);
    }

    [Fact]
    public void UnsubscribeTokenDetectsTampering()
    {
        var svc = MakeTokenService();
        var token = svc.Issue(Guid.NewGuid(), Guid.NewGuid(), "marketing", DateTimeOffset.UtcNow, TimeSpan.FromDays(30));
        var tampered = token[..^2] + "AA";
        var result = svc.Validate(tampered, DateTimeOffset.UtcNow);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void UnsubscribeTokenExpires()
    {
        var svc = MakeTokenService(lifetimeDays: 1);
        var issued = DateTimeOffset.UtcNow.AddDays(-5);
        var token = svc.Issue(Guid.NewGuid(), Guid.NewGuid(), "marketing", issued, TimeSpan.FromDays(1));
        var result = svc.Validate(token, DateTimeOffset.UtcNow);
        Assert.False(result.IsValid);
        Assert.Equal("expired", result.Reason);
    }

    [Fact]
    public void WebhookSignatureValidatesGoodSignature()
    {
        var svc = MakeWebhookService();
        var body = "{\"kind\":\"delivered\"}";
        var now = DateTimeOffset.UtcNow;
        var sig = svc.Sign(body, now);
        // sig format: hex.ts. Split off ts.
        var parts = sig.Split('.');
        var result = svc.Verify(parts[0], parts[1], body, now, TimeSpan.FromMinutes(5));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void WebhookSignatureRejectsWrongBody()
    {
        var svc = MakeWebhookService();
        var body = "{\"kind\":\"delivered\"}";
        var now = DateTimeOffset.UtcNow;
        var sig = svc.Sign(body, now);
        var parts = sig.Split('.');
        var result = svc.Verify(parts[0], parts[1], "{\"kind\":\"bounced\"}", now, TimeSpan.FromMinutes(5));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void WebhookSignatureRejectsExpiredTimestamp()
    {
        var svc = MakeWebhookService();
        var body = "{}";
        var signedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var sig = svc.Sign(body, signedAt);
        var parts = sig.Split('.');
        var result = svc.Verify(parts[0], parts[1], body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        Assert.False(result.IsValid);
        Assert.Equal("expired_timestamp", result.Reason);
    }
}
