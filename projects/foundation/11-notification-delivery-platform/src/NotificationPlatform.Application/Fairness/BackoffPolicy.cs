namespace NotificationPlatform.Application.Fairness;

public interface IBackoffPolicy
{
    TimeSpan NextDelay(int attempt);
}

/// <summary>
/// Exponential backoff with equal jitter, capped at MaxMilliseconds.
/// Deterministic when constructed with a fixed seed (used in tests).
/// </summary>
public sealed class ExponentialBackoffPolicy : IBackoffPolicy
{
    private readonly int _baseMs;
    private readonly int _maxMs;
    private readonly Random _rng;

    public ExponentialBackoffPolicy(int baseMs, int maxMs, int? seed = null)
    {
        if (baseMs <= 0) throw new ArgumentOutOfRangeException(nameof(baseMs));
        if (maxMs < baseMs) throw new ArgumentOutOfRangeException(nameof(maxMs));
        _baseMs = baseMs;
        _maxMs = maxMs;
        _rng = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    }

    public TimeSpan NextDelay(int attempt)
    {
        if (attempt <= 0) attempt = 1;
        var expo = (long)_baseMs * (long)Math.Pow(2, Math.Min(attempt - 1, 20));
        var capped = (int)Math.Min(expo, _maxMs);
        var jitter = _rng.Next(0, capped / 2 + 1);
        return TimeSpan.FromMilliseconds(capped / 2 + jitter);
    }
}
