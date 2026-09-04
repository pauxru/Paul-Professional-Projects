using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;

namespace Northstar.Secrets.Infrastructure.Notifications;

public sealed class WebhookNotificationOptions
{
    public string SigningKey { get; set; } = "demo-only-not-a-real-secret-webhook-signing-key";
    public int MaxAttempts { get; set; } = 3;
}

public sealed record WebhookDelivery(
    Uri Destination,
    string Body,
    string Timestamp,
    string Signature,
    string CorrelationId);

public interface IWebhookTransport
{
    Task<bool> SendAsync(WebhookDelivery delivery, CancellationToken cancellationToken);
}

public sealed class SimulatedWebhookTransport : IWebhookTransport
{
    public Task<bool> SendAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(!delivery.Destination.Host.Contains(
            "fail", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record DeadLetterNotification(
    Guid RotationId,
    Guid ConsumerId,
    string Channel,
    int Attempts,
    string Reason,
    DateTimeOffset RecordedAt);

public interface IDeadLetterStore
{
    void Add(DeadLetterNotification notification);
    IReadOnlyList<DeadLetterNotification> List();
}

public sealed class InMemoryDeadLetterStore : IDeadLetterStore
{
    private readonly ConcurrentQueue<DeadLetterNotification> _notifications = new();
    public void Add(DeadLetterNotification notification) => _notifications.Enqueue(notification);
    public IReadOnlyList<DeadLetterNotification> List() => _notifications.ToArray();
}

public sealed class WebhookNotificationChannel(
    IWebhookTransport transport,
    IDeadLetterStore deadLetterStore,
    WebhookNotificationOptions options,
    IClock clock) : INotificationChannel
{
    public string Name => "webhook";

    public async Task<NotificationDeliveryResult> SendAsync(
        Consumer consumer,
        RotationNotice notice,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(consumer.WebhookUrl, UriKind.Absolute, out var destination))
        {
            return new NotificationDeliveryResult(false, 0, "Consumer has no valid webhook URL.");
        }

        var body = JsonSerializer.Serialize(new
        {
            eventType = "secret.rotation.pending",
            rotationId = notice.RotationId,
            secretReference = notice.SecretReference,
            strategy = notice.Strategy.ToString(),
            acknowledgementDeadline = notice.AcknowledgementDeadline,
            maintenanceWindowStart = notice.MaintenanceWindowStart
        });
        var timestamp = clock.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = ComputeSignature(options.SigningKey, timestamp, body);

        for (var attempt = 1; attempt <= Math.Max(1, options.MaxAttempts); attempt++)
        {
            var accepted = await transport.SendAsync(
                new WebhookDelivery(
                    destination,
                    body,
                    timestamp,
                    signature,
                    notice.CorrelationId),
                cancellationToken);
            if (accepted)
            {
                return new NotificationDeliveryResult(true, attempt, null);
            }
        }

        deadLetterStore.Add(new DeadLetterNotification(
            notice.RotationId,
            consumer.Id,
            Name,
            Math.Max(1, options.MaxAttempts),
            "Webhook delivery exhausted its bounded retry budget.",
            clock.UtcNow));
        return new NotificationDeliveryResult(
            false,
            Math.Max(1, options.MaxAttempts),
            "Webhook delivery moved to the dead-letter queue.");
    }

    public static string ComputeSignature(string signingKey, string timestamp, string body)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(signingKey));
        using var hmac = new HMACSHA256(key);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return "v1=" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static bool VerifySignature(
        string signingKey,
        string timestamp,
        string body,
        string suppliedSignature)
    {
        var expected = Encoding.ASCII.GetBytes(ComputeSignature(signingKey, timestamp, body));
        var supplied = Encoding.ASCII.GetBytes(suppliedSignature);
        return expected.Length == supplied.Length &&
               CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}

public sealed record InboxNotification(
    Guid ConsumerId,
    Guid RotationId,
    string SecretReference,
    DateTimeOffset CreatedAt);

public interface IInAppInbox
{
    void Add(InboxNotification notification);
    IReadOnlyList<InboxNotification> List(Guid consumerId);
}

public sealed class InMemoryInAppInbox : IInAppInbox
{
    private readonly ConcurrentQueue<InboxNotification> _notifications = new();
    public void Add(InboxNotification notification) => _notifications.Enqueue(notification);
    public IReadOnlyList<InboxNotification> List(Guid consumerId) =>
        _notifications.Where(x => x.ConsumerId == consumerId).ToArray();
}

public sealed class InAppNotificationChannel(IInAppInbox inbox, IClock clock)
    : INotificationChannel
{
    public string Name => "in-app";

    public Task<NotificationDeliveryResult> SendAsync(
        Consumer consumer,
        RotationNotice notice,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        inbox.Add(new InboxNotification(
            consumer.Id, notice.RotationId, notice.SecretReference, clock.UtcNow));
        return Task.FromResult(new NotificationDeliveryResult(true, 1, null));
    }
}

public sealed record SimulatedEmail(
    string Recipient,
    string Subject,
    string Body,
    DateTimeOffset CreatedAt);

public interface IEmailOutbox
{
    void Add(SimulatedEmail email);
    IReadOnlyList<SimulatedEmail> List();
}

public sealed class InMemoryEmailOutbox : IEmailOutbox
{
    private readonly ConcurrentQueue<SimulatedEmail> _emails = new();
    public void Add(SimulatedEmail email) => _emails.Enqueue(email);
    public IReadOnlyList<SimulatedEmail> List() => _emails.ToArray();
}

public sealed class EmailSimulatorNotificationChannel(IEmailOutbox outbox, IClock clock)
    : INotificationChannel
{
    public string Name => "email-simulator";

    public Task<NotificationDeliveryResult> SendAsync(
        Consumer consumer,
        RotationNotice notice,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(consumer.Email))
        {
            return Task.FromResult(
                new NotificationDeliveryResult(false, 0, "Consumer has no email address."));
        }

        outbox.Add(new SimulatedEmail(
            consumer.Email,
            $"Rotation pending for {notice.SecretName}",
            $"Refresh {notice.SecretReference} and acknowledge rotation {notice.RotationId}.",
            clock.UtcNow));
        return Task.FromResult(new NotificationDeliveryResult(true, 1, null));
    }
}
