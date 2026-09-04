using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;
using Northstar.Secrets.Infrastructure.ExternalStores;
using Northstar.Secrets.Infrastructure.Notifications;

namespace Northstar.Secrets.UnitTests;

public sealed class NotificationAndAdapterTests
{
    [Fact]
    public async Task WebhookNotification_SignsTimestampAndBodyWithHmac()
    {
        var transport = new RecordingTransport([true]);
        var options = new WebhookNotificationOptions
        {
            SigningKey = "demo-only-not-a-real-secret-webhook-test",
            MaxAttempts = 3
        };
        var channel = new WebhookNotificationChannel(
            transport,
            new InMemoryDeadLetterStore(),
            options,
            new FakeClock(new DateTimeOffset(2026, 9, 3, 8, 0, 0, TimeSpan.Zero)));

        var result = await channel.SendAsync(
            Consumer(),
            Notice(),
            CancellationToken.None);

        Assert.True(result.Accepted);
        var delivery = Assert.Single(transport.Deliveries);
        Assert.True(WebhookNotificationChannel.VerifySignature(
            options.SigningKey,
            delivery.Timestamp,
            delivery.Body,
            delivery.Signature));
    }

    [Fact]
    public async Task WebhookNotification_TransientFailures_RetriesThenSucceeds()
    {
        var transport = new RecordingTransport([false, false, true]);
        var channel = new WebhookNotificationChannel(
            transport,
            new InMemoryDeadLetterStore(),
            new WebhookNotificationOptions
            {
                SigningKey = "demo-only-not-a-real-secret-webhook-test",
                MaxAttempts = 3
            },
            new FakeClock(DateTimeOffset.UtcNow));

        var result = await channel.SendAsync(Consumer(), Notice(), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, transport.Deliveries.Count);
    }

    [Fact]
    public async Task WebhookNotification_ExhaustedRetries_WritesDeadLetter()
    {
        var deadLetters = new InMemoryDeadLetterStore();
        var channel = new WebhookNotificationChannel(
            new RecordingTransport([false, false, false]),
            deadLetters,
            new WebhookNotificationOptions
            {
                SigningKey = "demo-only-not-a-real-secret-webhook-test",
                MaxAttempts = 3
            },
            new FakeClock(DateTimeOffset.UtcNow));

        var result = await channel.SendAsync(Consumer(), Notice(), CancellationToken.None);

        Assert.False(result.Accepted);
        var deadLetter = Assert.Single(deadLetters.List());
        Assert.Equal(3, deadLetter.Attempts);
        Assert.Equal("webhook", deadLetter.Channel);
    }

    [Fact]
    public void AzureKeyVaultAdapter_MapsHierarchicalNameToVaultCompatibleName()
    {
        Assert.Equal(
            "orders--prod--database",
            AzureKeyVaultExternalSecretStore.ToVaultName("orders/prod/database"));
    }

    private static Consumer Consumer() =>
        new(
            Guid.NewGuid(),
            "orders-consumer",
            "orders",
            "https://consumer.example.invalid/rotate",
            "consumer@example.invalid");

    private static RotationNotice Notice() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "orders/prod/database",
            "@secret:orders/prod/database#v2",
            RotationStrategyKind.DualWrite,
            DateTimeOffset.UtcNow.AddMinutes(10),
            null,
            "correlation");

    private sealed class RecordingTransport(IEnumerable<bool> outcomes) : IWebhookTransport
    {
        private readonly Queue<bool> _outcomes = new(outcomes);
        public List<WebhookDelivery> Deliveries { get; } = [];

        public Task<bool> SendAsync(
            WebhookDelivery delivery,
            CancellationToken cancellationToken)
        {
            Deliveries.Add(delivery);
            return Task.FromResult(_outcomes.Count > 0 && _outcomes.Dequeue());
        }
    }
}
