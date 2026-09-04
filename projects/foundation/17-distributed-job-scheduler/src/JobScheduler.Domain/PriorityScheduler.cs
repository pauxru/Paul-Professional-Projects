namespace JobScheduler.Domain;

/// <summary>
/// Priority scheduling with aging. Higher <c>basePriority</c> means more important. To stop
/// low-priority work starving forever, a run's effective priority is boosted the longer it has
/// been waiting. Ordering is by effective priority (desc) then scheduled time (asc).
/// </summary>
public static class PriorityScheduler
{
    /// <summary>
    /// Effective priority = base + floor(minutesWaiting * agingBoostPerMinute). A zero or
    /// negative aging rate disables aging (strict priority).
    /// </summary>
    public static long EffectivePriority(int basePriority, TimeSpan waiting, double agingBoostPerMinute)
    {
        if (agingBoostPerMinute <= 0 || waiting <= TimeSpan.Zero)
        {
            return basePriority;
        }

        long boost = (long)Math.Floor(waiting.TotalMinutes * agingBoostPerMinute);
        return basePriority + boost;
    }

    /// <summary>
    /// Orders candidate runs the way a worker should pick them: highest effective priority first,
    /// oldest scheduled time as the tie-breaker.
    /// </summary>
    public static IReadOnlyList<T> Order<T>(
        IEnumerable<T> candidates,
        Func<T, int> basePriority,
        Func<T, DateTimeOffset> scheduledAt,
        DateTimeOffset now,
        double agingBoostPerMinute) =>
        candidates
            .OrderByDescending(c => EffectivePriority(basePriority(c), now - scheduledAt(c), agingBoostPerMinute))
            .ThenBy(scheduledAt)
            .ToList();
}
