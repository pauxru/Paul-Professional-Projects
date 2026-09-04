namespace NotificationPlatform.UnitTests;

using NotificationPlatform.Domain.Common;
using NotificationPlatform.Domain.Notifications;
using NotificationPlatform.Domain.Providers;
using Xunit;

public sealed class DomainInvariantTests
{
    private static Notification NewNotification(NotificationPriority priority = NotificationPriority.Marketing)
    {
        return new Notification(
            id: Guid.NewGuid(),
            tenantId: Guid.NewGuid(),
            recipientId: Guid.NewGuid(),
            templateKey: "welcome",
            channel: NotificationChannel.Email,
            category: NotificationCategory.Marketing,
            priority: priority,
            payloadJson: "{}",
            locale: "en",
            idempotencyKey: null,
            deduplicationKey: null,
            createdAt: DateTimeOffset.UtcNow,
            scheduledFor: null,
            maxAttempts: 5,
            correlationId: null);
    }

    [Fact]
    public void NewNotificationStartsQueued()
    {
        var n = NewNotification();
        Assert.Equal(NotificationStatus.Queued, n.Status);
    }

    [Fact]
    public void ScheduledNotificationStartsScheduled()
    {
        var n = new Notification(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "welcome", NotificationChannel.Email, NotificationCategory.Marketing,
            NotificationPriority.Marketing, "{}", "en", null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), 5, null);
        Assert.Equal(NotificationStatus.Scheduled, n.Status);
    }

    [Fact]
    public void InvalidTransitionThrows()
    {
        var n = NewNotification();
        Assert.Throws<DomainException>(() => n.CompleteRendering(1, null, "body", "x@y.com"));
    }

    [Fact]
    public void RenderingLifecycleWorks()
    {
        var n = NewNotification();
        n.BeginRendering();
        Assert.Equal(NotificationStatus.Rendering, n.Status);
        n.CompleteRendering(1, "Subj", "Body", "a@b.com");
        Assert.Equal(NotificationStatus.Dispatched, n.Status);
        Assert.Equal("Body", n.RenderedBody);
    }

    [Fact]
    public void TransactionalCanBypassQuietHours()
    {
        Assert.True(NewNotification(NotificationPriority.Transactional).CanBypassQuietHours());
        Assert.False(NewNotification(NotificationPriority.Marketing).CanBypassQuietHours());
    }

    [Fact]
    public void MaxAttemptsMustBeInRange()
    {
        Assert.Throws<ArgumentException>(() => new Notification(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "x", NotificationChannel.Sms, NotificationCategory.Transactional,
            NotificationPriority.Transactional, "{}", "en", null, null,
            DateTimeOffset.UtcNow, null, 0, null));
    }

    [Fact]
    public void CircuitOpensAfterThreshold()
    {
        var ph = new ProviderHealth("smtp", NotificationChannel.Email);
        var now = DateTimeOffset.UtcNow;
        ph.RecordFailure(3, now);
        ph.RecordFailure(3, now);
        Assert.Equal(CircuitState.Closed, ph.State);
        ph.RecordFailure(3, now);
        Assert.Equal(CircuitState.Open, ph.State);
        Assert.False(ph.CanSend());
    }

    [Fact]
    public void CircuitHalfOpensAfterCooldown()
    {
        var ph = new ProviderHealth("smtp", NotificationChannel.Email);
        var t0 = DateTimeOffset.UtcNow;
        ph.RecordFailure(1, t0);
        Assert.True(ph.ShouldTrip(t0 + TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)));
        Assert.Equal(CircuitState.HalfOpen, ph.State);
    }

    [Fact]
    public void CircuitClosesAfterHalfOpenSuccesses()
    {
        var ph = new ProviderHealth("smtp", NotificationChannel.Email);
        var t0 = DateTimeOffset.UtcNow;
        ph.RecordFailure(1, t0);
        ph.ShouldTrip(t0 + TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30));
        ph.RecordSuccess(2);
        Assert.Equal(CircuitState.HalfOpen, ph.State);
        ph.RecordSuccess(2);
        Assert.Equal(CircuitState.Closed, ph.State);
    }

    [Fact]
    public void HalfOpenFailureReopens()
    {
        var ph = new ProviderHealth("smtp", NotificationChannel.Email);
        var t0 = DateTimeOffset.UtcNow;
        ph.RecordFailure(1, t0);
        ph.ShouldTrip(t0 + TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30));
        ph.RecordFailure(1, t0 + TimeSpan.FromSeconds(61));
        Assert.Equal(CircuitState.Open, ph.State);
    }
}
