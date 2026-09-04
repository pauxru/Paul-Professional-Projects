using System.Security.Cryptography;
using System.Text;
using SubscriptionBilling.Domain;

namespace SubscriptionBilling.Application;

public sealed class WebhookSignatureService(
    IClock clock,
    IWebhookReplayStore replayStore,
    string signingSecret,
    TimeSpan tolerance)
{
    public string Sign(string rawBody, DateTimeOffset timestamp)
    {
        var unix = timestamp.ToUnixTimeSeconds();
        var digest = ComputeDigest(signingSecret, unix, rawBody);
        return $"t={unix},v1={digest}";
    }

    public async Task<bool> VerifyAsync(
        string rawBody,
        string signatureHeader,
        string nonce,
        CancellationToken cancellationToken)
    {
        if (!TryParse(signatureHeader, out var unix, out var suppliedDigest))
        {
            return false;
        }

        var timestamp = DateTimeOffset.FromUnixTimeSeconds(unix);
        if ((clock.UtcNow - timestamp).Duration() > tolerance)
        {
            return false;
        }

        var expectedDigest = ComputeDigest(signingSecret, unix, rawBody);
        var suppliedBytes = Encoding.ASCII.GetBytes(suppliedDigest);
        var expectedBytes = Encoding.ASCII.GetBytes(expectedDigest);
        if (suppliedBytes.Length != expectedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes))
        {
            return false;
        }

        return await replayStore.TryRecordAsync(
            nonce,
            clock.UtcNow.Add(tolerance),
            cancellationToken);
    }

    private static string ComputeDigest(string secret, long unix, string rawBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var payload = Encoding.UTF8.GetBytes($"{unix}.{rawBody}");
        return Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();
    }

    private static bool TryParse(string header, out long unix, out string digest)
    {
        unix = 0;
        digest = string.Empty;
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("t=", StringComparison.Ordinal) &&
                long.TryParse(part[2..], out var parsed))
            {
                unix = parsed;
            }
            else if (part.StartsWith("v1=", StringComparison.Ordinal))
            {
                digest = part[3..];
            }
        }

        return unix > 0 && digest.Length == 64;
    }
}

public sealed record DunningPolicy(IReadOnlyList<int> RetryDays)
{
    public static DunningPolicy Standard { get; } = new([1, 3, 5, 7]);
}

public sealed class DunningCase
{
    public DunningCase(
        Guid invoiceId,
        Guid subscriptionId,
        DateTimeOffset initialFailureAt,
        DunningPolicy policy)
    {
        if (invoiceId == Guid.Empty || subscriptionId == Guid.Empty)
        {
            throw new DomainException("Dunning invoice and subscription ids are required.");
        }

        if (policy.RetryDays.Count == 0 ||
            policy.RetryDays.Any(day => day <= 0) ||
            !policy.RetryDays.SequenceEqual(policy.RetryDays.Order()))
        {
            throw new DomainException("Dunning retry days must be positive and ordered.");
        }

        InvoiceId = invoiceId;
        SubscriptionId = subscriptionId;
        InitialFailureAt = initialFailureAt;
        Policy = policy;
    }

    public Guid InvoiceId { get; }
    public Guid SubscriptionId { get; }
    public DateTimeOffset InitialFailureAt { get; }
    public DunningPolicy Policy { get; }
    public int AttemptsCompleted { get; private set; }
    public bool Recovered { get; private set; }
    public bool Escalated { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }

    public DateTimeOffset? NextAttemptAt =>
        Recovered || Escalated || AttemptsCompleted >= Policy.RetryDays.Count
            ? null
            : InitialFailureAt.AddDays(Policy.RetryDays[AttemptsCompleted]);

    public bool IsDue(DateTimeOffset now) =>
        NextAttemptAt is not null && now >= NextAttemptAt.Value;

    public void RecordAttempt(bool succeeded, DateTimeOffset attemptedAt)
    {
        if (!IsDue(attemptedAt))
        {
            throw new DomainException("Dunning attempt is not due.");
        }

        AttemptsCompleted++;
        LastAttemptAt = attemptedAt;
        if (succeeded)
        {
            Recovered = true;
        }
        else if (AttemptsCompleted >= Policy.RetryDays.Count)
        {
            Escalated = true;
        }
    }
}

public enum OutboundWebhookStatus
{
    Pending,
    Delivered,
    DeadLetter
}

public sealed class OutboundWebhookDelivery
{
    public OutboundWebhookDelivery(
        Guid id,
        Uri endpoint,
        string eventType,
        string payload,
        DateTimeOffset createdAt,
        int maxAttempts = 5)
    {
        if (id == Guid.Empty || maxAttempts <= 0)
        {
            throw new DomainException("Outbound webhook id and positive max attempts are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        Id = id;
        Endpoint = endpoint;
        EventType = eventType;
        Payload = payload;
        CreatedAt = createdAt;
        NextAttemptAt = createdAt;
        MaxAttempts = maxAttempts;
        Status = OutboundWebhookStatus.Pending;
    }

    public Guid Id { get; }
    public Uri Endpoint { get; }
    public string EventType { get; }
    public string Payload { get; }
    public DateTimeOffset CreatedAt { get; }
    public int MaxAttempts { get; }
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public OutboundWebhookStatus Status { get; private set; }
    public string? LastError { get; private set; }

    public void RecordSuccess()
    {
        EnsurePending();
        Attempts++;
        Status = OutboundWebhookStatus.Delivered;
        LastError = null;
    }

    public void RecordFailure(DateTimeOffset attemptedAt, string error)
    {
        EnsurePending();
        Attempts++;
        LastError = error;
        if (Attempts >= MaxAttempts)
        {
            Status = OutboundWebhookStatus.DeadLetter;
            return;
        }

        var shift = Math.Min(Attempts, 20);
        var backoffSeconds = 1L << shift;
        NextAttemptAt = attemptedAt.AddSeconds(backoffSeconds);
    }

    private void EnsurePending()
    {
        if (Status != OutboundWebhookStatus.Pending)
        {
            throw new DomainException("Only pending webhook deliveries can be changed.");
        }
    }
}
