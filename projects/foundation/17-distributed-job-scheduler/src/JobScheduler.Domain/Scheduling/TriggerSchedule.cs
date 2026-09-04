namespace JobScheduler.Domain.Scheduling;

/// <summary>
/// The result of evaluating a trigger: the concrete fire instants that should become runs,
/// separated so callers can label how each occurrence arose (on-time vs recovered misfire).
/// </summary>
public sealed record DueOccurrences(
    IReadOnlyList<DateTimeOffset> OnTime,
    IReadOnlyList<DateTimeOffset> Recovered)
{
    public static readonly DueOccurrences None = new([], []);

    public IReadOnlyList<DateTimeOffset> All =>
        OnTime.Concat(Recovered).OrderBy(x => x).ToList();

    public int Count => OnTime.Count + Recovered.Count;
}

/// <summary>
/// Immutable description of when a job should fire, decoupled from persistence so it can be
/// unit-tested in isolation. Encapsulates the trigger kind, timezone, misfire policy and
/// catch-up window, and computes exactly which occurrences are due between two instants.
/// </summary>
public sealed class TriggerSchedule
{
    private const int HardOccurrenceCap = 100_000;

    public TriggerType Type { get; }
    public CronExpression? Cron { get; }
    public TimeSpan? Interval { get; }
    public DateTimeOffset? RunAt { get; }
    public TimeZoneInfo TimeZone { get; }
    public MisfirePolicy Misfire { get; }
    public TimeSpan CatchUpWindow { get; }
    public int MaxCatchUp { get; }

    public TriggerSchedule(
        TriggerType type,
        TimeZoneInfo timeZone,
        CronExpression? cron = null,
        TimeSpan? interval = null,
        DateTimeOffset? runAt = null,
        MisfirePolicy misfire = MisfirePolicy.FireNow,
        TimeSpan? catchUpWindow = null,
        int maxCatchUp = 100)
    {
        TimeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        Type = type;
        Cron = cron;
        Interval = interval;
        RunAt = runAt;
        Misfire = misfire;
        CatchUpWindow = catchUpWindow ?? TimeSpan.FromMinutes(5);
        MaxCatchUp = Math.Max(1, maxCatchUp);

        switch (type)
        {
            case TriggerType.Cron when cron is null:
                throw new ArgumentException("Cron trigger requires a cron expression.", nameof(cron));
            case TriggerType.Interval when interval is null or { Ticks: <= 0 }:
                throw new ArgumentException("Interval trigger requires a positive interval.", nameof(interval));
            case TriggerType.OneOff when runAt is null:
                throw new ArgumentException("One-off trigger requires a runAt instant.", nameof(runAt));
        }
    }

    /// <summary>
    /// The next scheduled instant strictly after <paramref name="after"/>, or null for
    /// manual triggers / one-offs already in the past.
    /// </summary>
    public DateTimeOffset? NextFireAfter(DateTimeOffset after) => Type switch
    {
        TriggerType.Cron => Cron!.GetNextOccurrence(after, TimeZone),
        TriggerType.Interval => after + Interval!.Value,
        TriggerType.OneOff => RunAt > after ? RunAt : null,
        _ => null
    };

    /// <summary>
    /// Computes the occurrences that are due in the window <c>(lastFire, now]</c> and applies the
    /// misfire policy. Occurrences within <see cref="CatchUpWindow"/> of <paramref name="now"/> are
    /// treated as on-time; older ones are misfires handled per <see cref="Misfire"/>.
    /// </summary>
    public DueOccurrences ComputeDue(DateTimeOffset lastFire, DateTimeOffset now)
    {
        if (Type == TriggerType.Manual)
        {
            return DueOccurrences.None;
        }

        var occurrences = Enumerate(lastFire, now);
        if (occurrences.Count == 0)
        {
            return DueOccurrences.None;
        }

        var cutoff = now - CatchUpWindow;
        var onTime = occurrences.Where(o => o >= cutoff).ToList();
        var misfired = occurrences.Where(o => o < cutoff).ToList();

        if (misfired.Count == 0)
        {
            return new DueOccurrences(onTime, []);
        }

        var recovered = Misfire switch
        {
            MisfirePolicy.SkipToNext => new List<DateTimeOffset>(),
            // Collapse the whole missed backlog into a single catch-up run.
            MisfirePolicy.FireNow => [misfired[^1]],
            // Replay every missed occurrence, capped, keeping the most recent.
            MisfirePolicy.RunAllMissed => misfired.Count > MaxCatchUp
                ? misfired.GetRange(misfired.Count - MaxCatchUp, MaxCatchUp)
                : misfired,
            _ => misfired
        };

        return new DueOccurrences(onTime, recovered);
    }

    private List<DateTimeOffset> Enumerate(DateTimeOffset lastFire, DateTimeOffset now)
    {
        var result = new List<DateTimeOffset>();
        switch (Type)
        {
            case TriggerType.Cron:
                var cursor = lastFire;
                for (int i = 0; i < HardOccurrenceCap; i++)
                {
                    var next = Cron!.GetNextOccurrence(cursor, TimeZone);
                    if (next is null || next > now)
                    {
                        break;
                    }
                    result.Add(next.Value);
                    cursor = next.Value;
                }
                break;

            case TriggerType.Interval:
                var t = lastFire + Interval!.Value;
                for (int i = 0; i < HardOccurrenceCap && t <= now; i++)
                {
                    result.Add(t);
                    t += Interval.Value;
                }
                break;

            case TriggerType.OneOff:
                if (RunAt > lastFire && RunAt <= now)
                {
                    result.Add(RunAt!.Value);
                }
                break;
        }

        return result;
    }
}
