namespace NotificationPlatform.Domain.Providers;

using NotificationPlatform.Domain.Common;

public sealed class ProviderHealth
{
    public Guid Id { get; private set; }
    public string ProviderName { get; private set; } = string.Empty;
    public NotificationChannel Channel { get; private set; }
    public CircuitState State { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public DateTimeOffset LastFailureAt { get; private set; }
    public DateTimeOffset? OpenedAt { get; private set; }
    public int HalfOpenSuccesses { get; private set; }

    private ProviderHealth() { }

    public ProviderHealth(string providerName, NotificationChannel channel)
    {
        Id = Guid.NewGuid();
        ProviderName = providerName;
        Channel = channel;
        State = CircuitState.Closed;
    }

    public void RecordSuccess(int halfOpenThreshold)
    {
        if (State == CircuitState.HalfOpen)
        {
            HalfOpenSuccesses++;
            if (HalfOpenSuccesses >= halfOpenThreshold)
            {
                State = CircuitState.Closed;
                ConsecutiveFailures = 0;
                OpenedAt = null;
                HalfOpenSuccesses = 0;
            }
        }
        else
        {
            ConsecutiveFailures = 0;
            State = CircuitState.Closed;
            OpenedAt = null;
        }
    }

    public void RecordFailure(int failureThreshold, DateTimeOffset now)
    {
        ConsecutiveFailures++;
        LastFailureAt = now;
        HalfOpenSuccesses = 0;
        if (State == CircuitState.HalfOpen)
        {
            State = CircuitState.Open;
            OpenedAt = now;
        }
        else if (ConsecutiveFailures >= failureThreshold)
        {
            State = CircuitState.Open;
            OpenedAt = now;
        }
    }

    public bool ShouldTrip(DateTimeOffset now, TimeSpan openDuration)
    {
        if (State == CircuitState.Open && OpenedAt.HasValue && now - OpenedAt.Value >= openDuration)
        {
            State = CircuitState.HalfOpen;
            HalfOpenSuccesses = 0;
            return true;
        }
        return false;
    }

    public bool CanSend() => State != CircuitState.Open;
}

public sealed class TenantUsageCounter
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public int YearMonth { get; private set; }
    public int Count { get; private set; }

    private TenantUsageCounter() { }

    public TenantUsageCounter(Guid tenantId, int yearMonth, int count)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        YearMonth = yearMonth;
        Count = count;
    }

    public void Increment() => Count++;
}

public sealed class RecipientFrequencyCounter
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid RecipientId { get; private set; }
    public NotificationCategory Category { get; private set; }
    public string DateKey { get; private set; } = string.Empty; // yyyy-MM-dd in recipient tz
    public int Count { get; private set; }

    private RecipientFrequencyCounter() { }

    public RecipientFrequencyCounter(Guid tenantId, Guid recipientId, NotificationCategory category, string dateKey, int count)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        RecipientId = recipientId;
        Category = category;
        DateKey = dateKey;
        Count = count;
    }

    public void Increment() => Count++;
}
