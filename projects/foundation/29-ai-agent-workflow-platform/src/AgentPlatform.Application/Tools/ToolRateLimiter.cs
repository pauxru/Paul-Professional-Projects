using System.Collections.Concurrent;
using AgentPlatform.Domain.Abstractions;

namespace AgentPlatform.Application.Tools;

/// <summary>
/// Simple in-memory sliding-window rate limiter, partitioned by an arbitrary key
/// (tenant + tool). Uses <see cref="IClock"/> so limits are testable with a fake clock.
/// </summary>
public sealed class ToolRateLimiter
{
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ToolRateLimiter(IClock clock) => _clock = clock;

    public bool TryAcquire(string key, int perMinute)
    {
        if (perMinute <= 0) return true;
        var now = _clock.UtcNow;
        var windowStart = now - TimeSpan.FromMinutes(1);

        lock (_gate)
        {
            var queue = _hits.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
            while (queue.Count > 0 && queue.Peek() < windowStart) queue.Dequeue();
            if (queue.Count >= perMinute) return false;
            queue.Enqueue(now);
            return true;
        }
    }
}
