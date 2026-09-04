using JobScheduler.Domain.Scheduling;

namespace JobScheduler.Domain.Entities;

/// <summary>
/// A job definition: the template from which runs are created. Carries scheduling, retry,
/// concurrency and routing configuration. Behaviour (schedule/retry construction, validation)
/// lives here so the rules cannot be bypassed by the persistence layer.
/// </summary>
public sealed class JobDefinition
{
    private JobDefinition() { } // EF

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Name { get; private set; } = string.Empty;
    public string HandlerType { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = "{}";
    public string Queue { get; private set; } = "default";
    public int Priority { get; private set; }
    public int ConcurrencyLimit { get; private set; } = 1;
    public bool Singleton { get; private set; }
    public string Owner { get; private set; } = "unknown";
    public string TagsCsv { get; private set; } = string.Empty;
    public string DependsOnCsv { get; private set; } = string.Empty;
    public bool Enabled { get; private set; } = true;

    // Retry configuration
    public RetryStrategy RetryStrategy { get; private set; } = RetryStrategy.ExponentialJitter;
    public double RetryBaseSeconds { get; private set; } = 5;
    public double RetryMaxSeconds { get; private set; } = 300;
    public double RetryJitter { get; private set; } = 0.2;
    public int MaxAttempts { get; private set; } = 3;

    // Execution limits
    public int TimeoutSeconds { get; private set; } = 300;
    public int? DeadlineSeconds { get; private set; }

    // Trigger configuration
    public TriggerType TriggerType { get; private set; } = TriggerType.Manual;
    public string? CronExpression { get; private set; }
    public int? IntervalSeconds { get; private set; }
    public DateTimeOffset? RunAt { get; private set; }
    public string TimeZoneId { get; private set; } = "UTC";
    public MisfirePolicy MisfirePolicy { get; private set; } = MisfirePolicy.FireNow;
    public int CatchUpWindowSeconds { get; private set; } = 300;
    public int MaxCatchUp { get; private set; } = 100;

    // Bookkeeping
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? LastFireAt { get; private set; }
    public int Version { get; private set; }

    public IReadOnlyList<string> Tags =>
        TagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public IReadOnlyList<string> DependsOn =>
        DependsOnCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static JobDefinition Create(
        string name,
        string handlerType,
        DateTimeOffset now,
        string payloadJson = "{}",
        string queue = "default",
        int priority = 0,
        int concurrencyLimit = 1,
        bool singleton = false,
        string owner = "unknown",
        IEnumerable<string>? tags = null,
        IEnumerable<string>? dependsOn = null,
        RetryStrategy retryStrategy = RetryStrategy.ExponentialJitter,
        double retryBaseSeconds = 5,
        double retryMaxSeconds = 300,
        double retryJitter = 0.2,
        int maxAttempts = 3,
        int timeoutSeconds = 300,
        int? deadlineSeconds = null,
        TriggerType triggerType = TriggerType.Manual,
        string? cronExpression = null,
        int? intervalSeconds = null,
        DateTimeOffset? runAt = null,
        string timeZoneId = "UTC",
        MisfirePolicy misfirePolicy = MisfirePolicy.FireNow,
        int catchUpWindowSeconds = 300,
        int maxCatchUp = 100)
    {
        var def = new JobDefinition
        {
            Id = Guid.NewGuid(),
            Name = Guard.NotBlank(name, nameof(name)),
            HandlerType = Guard.NotBlank(handlerType, nameof(handlerType)),
            PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            Queue = string.IsNullOrWhiteSpace(queue) ? "default" : queue,
            Priority = priority,
            ConcurrencyLimit = Math.Max(1, concurrencyLimit),
            Singleton = singleton,
            Owner = owner,
            TagsCsv = tags is null ? string.Empty : string.Join(',', tags),
            DependsOnCsv = dependsOn is null ? string.Empty : string.Join(',', dependsOn),
            RetryStrategy = retryStrategy,
            RetryBaseSeconds = retryBaseSeconds,
            RetryMaxSeconds = retryMaxSeconds,
            RetryJitter = retryJitter,
            MaxAttempts = Math.Max(1, maxAttempts),
            TimeoutSeconds = Math.Max(1, timeoutSeconds),
            DeadlineSeconds = deadlineSeconds,
            TriggerType = triggerType,
            CronExpression = cronExpression,
            IntervalSeconds = intervalSeconds,
            RunAt = runAt,
            TimeZoneId = string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId,
            MisfirePolicy = misfirePolicy,
            CatchUpWindowSeconds = Math.Max(0, catchUpWindowSeconds),
            MaxCatchUp = Math.Max(1, maxCatchUp),
            CreatedAt = now,
            UpdatedAt = now
        };

        def.ValidateTrigger();
        return def;
    }

    public void Enable(DateTimeOffset now)
    {
        Enabled = true;
        Touch(now);
    }

    public void Disable(DateTimeOffset now)
    {
        Enabled = false;
        Touch(now);
    }

    public void MarkFired(DateTimeOffset at)
    {
        if (LastFireAt is null || at > LastFireAt)
        {
            LastFireAt = at;
        }
    }

    public void UpdateConfiguration(
        string payloadJson,
        int priority,
        int concurrencyLimit,
        bool singleton,
        int maxAttempts,
        int timeoutSeconds,
        DateTimeOffset now)
    {
        PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson;
        Priority = priority;
        ConcurrencyLimit = Math.Max(1, concurrencyLimit);
        Singleton = singleton;
        MaxAttempts = Math.Max(1, maxAttempts);
        TimeoutSeconds = Math.Max(1, timeoutSeconds);
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }

    public TimeZoneInfo ResolveTimeZone() => TimeZoneResolver.Resolve(TimeZoneId);

    public RetryPolicy BuildRetryPolicy() => new(
        RetryStrategy,
        TimeSpan.FromSeconds(RetryBaseSeconds),
        TimeSpan.FromSeconds(RetryMaxSeconds),
        MaxAttempts,
        RetryJitter);

    public TriggerSchedule BuildSchedule()
    {
        var tz = ResolveTimeZone();
        return TriggerType switch
        {
            TriggerType.Cron => new TriggerSchedule(
                TriggerType.Cron, tz,
                cron: Scheduling.CronExpression.Parse(CronExpression!),
                misfire: MisfirePolicy,
                catchUpWindow: TimeSpan.FromSeconds(CatchUpWindowSeconds),
                maxCatchUp: MaxCatchUp),
            TriggerType.Interval => new TriggerSchedule(
                TriggerType.Interval, tz,
                interval: TimeSpan.FromSeconds(IntervalSeconds!.Value),
                misfire: MisfirePolicy,
                catchUpWindow: TimeSpan.FromSeconds(CatchUpWindowSeconds),
                maxCatchUp: MaxCatchUp),
            TriggerType.OneOff => new TriggerSchedule(
                TriggerType.OneOff, tz,
                runAt: RunAt,
                misfire: MisfirePolicy,
                catchUpWindow: TimeSpan.FromSeconds(CatchUpWindowSeconds),
                maxCatchUp: MaxCatchUp),
            _ => new TriggerSchedule(TriggerType.Manual, tz)
        };
    }

    private void ValidateTrigger()
    {
        if (!TimeZoneResolver.IsValid(TimeZoneId))
        {
            throw new ArgumentException($"Unknown time zone '{TimeZoneId}'.");
        }

        switch (TriggerType)
        {
            case TriggerType.Cron:
                if (string.IsNullOrWhiteSpace(CronExpression))
                {
                    throw new ArgumentException("Cron trigger requires a cron expression.");
                }
                Scheduling.CronExpression.Parse(CronExpression); // throws on invalid
                break;
            case TriggerType.Interval:
                if (IntervalSeconds is null or <= 0)
                {
                    throw new ArgumentException("Interval trigger requires a positive intervalSeconds.");
                }
                break;
            case TriggerType.OneOff:
                if (RunAt is null)
                {
                    throw new ArgumentException("One-off trigger requires a runAt instant.");
                }
                break;
        }
    }
}
