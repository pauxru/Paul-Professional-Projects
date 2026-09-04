namespace JobScheduler.Domain;

/// <summary>
/// Per-job retry and backoff policy. Pure and deterministic: the jitter sample is
/// injectable so the sequence can be asserted in tests without touching a clock or RNG.
/// </summary>
public sealed record RetryPolicy
{
    public RetryStrategy Strategy { get; }
    public TimeSpan BaseDelay { get; }
    public TimeSpan MaxDelay { get; }
    public int MaxAttempts { get; }
    public double JitterFraction { get; }

    public RetryPolicy(
        RetryStrategy strategy,
        TimeSpan baseDelay,
        TimeSpan maxDelay,
        int maxAttempts,
        double jitterFraction = 0.2)
    {
        if (baseDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDelay), "Base delay cannot be negative.");
        }
        if (maxDelay < baseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDelay), "Max delay must be >= base delay.");
        }
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Max attempts must be >= 1.");
        }
        if (jitterFraction is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterFraction), "Jitter fraction must be within [0,1].");
        }

        Strategy = strategy;
        BaseDelay = baseDelay;
        MaxDelay = maxDelay;
        MaxAttempts = maxAttempts;
        JitterFraction = jitterFraction;
    }

    /// <summary>
    /// True while more attempts remain. <paramref name="completedAttempts"/> is the number of
    /// attempts that have already run (and failed). When this returns false the run is poison
    /// and must be dead-lettered.
    /// </summary>
    public bool ShouldRetry(int completedAttempts) => completedAttempts < MaxAttempts;

    /// <summary>
    /// Delay before the next attempt. <paramref name="completedAttempts"/> is 1-based (1 after the
    /// first failure). <paramref name="jitterSample"/> in [0,1] selects a point within the jitter
    /// band; when omitted a cryptographically-uniform sample is drawn.
    /// </summary>
    public TimeSpan NextDelay(int completedAttempts, double? jitterSample = null)
    {
        if (completedAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(completedAttempts));
        }

        double baseSeconds = BaseDelay.TotalSeconds;
        double raw = Strategy switch
        {
            RetryStrategy.Fixed => baseSeconds,
            RetryStrategy.Exponential => baseSeconds * Math.Pow(2, completedAttempts - 1),
            RetryStrategy.ExponentialJitter => ApplyJitter(baseSeconds * Math.Pow(2, completedAttempts - 1), jitterSample),
            _ => baseSeconds
        };

        double capped = Math.Min(raw, MaxDelay.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Max(0, capped));
    }

    private double ApplyJitter(double value, double? sample)
    {
        double s = sample ?? Random.Shared.NextDouble();
        s = Math.Clamp(s, 0, 1);
        // Symmetric band: value * (1 +/- JitterFraction).
        double factor = 1 - JitterFraction + (2 * JitterFraction * s);
        return value * factor;
    }
}
