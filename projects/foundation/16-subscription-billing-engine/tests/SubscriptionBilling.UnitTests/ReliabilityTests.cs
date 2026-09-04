using SubscriptionBilling.Application;
using SubscriptionBilling.Domain;

namespace SubscriptionBilling.UnitTests;

public sealed class ReliabilityTests
{
    [Theory]
    [InlineData("pm_success", PaymentOutcome.Succeeded)]
    [InlineData("pm_insufficient", PaymentOutcome.InsufficientFunds)]
    [InlineData("pm_expired", PaymentOutcome.ExpiredCard)]
    [InlineData("pm_decline", PaymentOutcome.HardDecline)]
    [InlineData("pm_timeout", PaymentOutcome.GatewayTimeout)]
    public async Task PaymentSimulator_Token_SelectsExpectedOutcome(
        string token,
        PaymentOutcome expected)
    {
        var simulator = new SubscriptionBilling.Infrastructure.PaymentSimulator(
            new SubscriptionBilling.Infrastructure.GuidGenerator());

        var result = await simulator.ChargeAsync(
            new PaymentRequest(
                Guid.NewGuid(),
                new Money(1_000, "USD"),
                token,
                Guid.NewGuid().ToString("N")),
            CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task WebhookSignature_ValidSignature_IsAcceptedOnce()
    {
        var clock = new FakeClock(At(2026, 1, 1));
        var replay = new InMemoryReplayStore();
        var service = new WebhookSignatureService(
            clock,
            replay,
            "test-only-signing-secret-at-least-thirty-two-characters",
            TimeSpan.FromMinutes(5));
        var body = """{"eventType":"payment.succeeded"}""";
        var signature = service.Sign(body, clock.UtcNow);

        var first = await service.VerifyAsync(body, signature, "nonce-1", CancellationToken.None);
        var replayed = await service.VerifyAsync(body, signature, "nonce-1", CancellationToken.None);

        Assert.True(first);
        Assert.False(replayed);
    }

    [Fact]
    public async Task WebhookSignature_ModifiedBody_IsRejected()
    {
        var clock = new FakeClock(At(2026, 1, 1));
        var service = new WebhookSignatureService(
            clock,
            new InMemoryReplayStore(),
            "test-only-signing-secret-at-least-thirty-two-characters",
            TimeSpan.FromMinutes(5));
        var signature = service.Sign("original", clock.UtcNow);

        Assert.False(await service.VerifyAsync(
            "modified",
            signature,
            "nonce-2",
            CancellationToken.None));
    }

    [Fact]
    public async Task WebhookSignature_ExpiredTimestamp_IsRejected()
    {
        var clock = new FakeClock(At(2026, 1, 1));
        var service = new WebhookSignatureService(
            clock,
            new InMemoryReplayStore(),
            "test-only-signing-secret-at-least-thirty-two-characters",
            TimeSpan.FromMinutes(5));
        var signature = service.Sign("payload", clock.UtcNow);
        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.False(await service.VerifyAsync(
            "payload",
            signature,
            "nonce-3",
            CancellationToken.None));
    }

    [Fact]
    public void DunningSchedule_DaysOneThreeFiveSeven_AreEnforced()
    {
        var start = At(2026, 1, 1);
        var dunning = new DunningCase(
            Guid.NewGuid(),
            Guid.NewGuid(),
            start,
            DunningPolicy.Standard);

        Assert.Equal(start.AddDays(1), dunning.NextAttemptAt);
        Assert.False(dunning.IsDue(start.AddHours(23)));
        dunning.RecordAttempt(false, start.AddDays(1));
        Assert.Equal(start.AddDays(3), dunning.NextAttemptAt);
        dunning.RecordAttempt(false, start.AddDays(3));
        Assert.Equal(start.AddDays(5), dunning.NextAttemptAt);
        dunning.RecordAttempt(false, start.AddDays(5));
        Assert.Equal(start.AddDays(7), dunning.NextAttemptAt);
        dunning.RecordAttempt(false, start.AddDays(7));
        Assert.True(dunning.Escalated);
        Assert.Null(dunning.NextAttemptAt);
    }

    [Fact]
    public void DunningSuccessfulRetry_MarksRecovery()
    {
        var start = At(2026, 1, 1);
        var dunning = new DunningCase(
            Guid.NewGuid(),
            Guid.NewGuid(),
            start,
            DunningPolicy.Standard);

        dunning.RecordAttempt(true, start.AddDays(1));

        Assert.True(dunning.Recovered);
        Assert.False(dunning.Escalated);
    }

    [Fact]
    public void OutboundWebhook_FailuresRetryThenDeadLetter()
    {
        var start = At(2026, 1, 1);
        var delivery = new OutboundWebhookDelivery(
            Guid.NewGuid(),
            new Uri("https://fail.example.invalid/hook"),
            "invoice.created",
            "{}",
            start,
            maxAttempts: 2);

        delivery.RecordFailure(start, "failed");
        Assert.Equal(OutboundWebhookStatus.Pending, delivery.Status);
        Assert.Equal(start.AddSeconds(2), delivery.NextAttemptAt);
        delivery.RecordFailure(delivery.NextAttemptAt, "failed again");

        Assert.Equal(OutboundWebhookStatus.DeadLetter, delivery.Status);
        Assert.Equal(2, delivery.Attempts);
    }

    [Fact]
    public void OutboundWebhook_Success_CompletesDelivery()
    {
        var delivery = new OutboundWebhookDelivery(
            Guid.NewGuid(),
            new Uri("https://receiver.example.invalid/hook"),
            "invoice.paid",
            "{}",
            At(2026, 1, 1));

        delivery.RecordSuccess();

        Assert.Equal(OutboundWebhookStatus.Delivered, delivery.Status);
        Assert.Equal(1, delivery.Attempts);
    }

    internal sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;
        public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
        public void Set(DateTimeOffset value) => UtcNow = value;
    }

    private sealed class InMemoryReplayStore : IWebhookReplayStore
    {
        private readonly HashSet<string> _nonces = new(StringComparer.Ordinal);

        public Task<bool> TryRecordAsync(
            string nonce,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken) =>
            Task.FromResult(_nonces.Add(nonce));
    }

    internal static DateTimeOffset At(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);
}
