using System.Collections.Concurrent;
using System.Diagnostics;
using Collab.Application.Options;
using Microsoft.Extensions.Options;

namespace Collab.Api.Realtime;

public enum RateDecision
{
    Allowed = 0,
    Throttled = 1,
    Disconnect = 2
}

/// <summary>
/// Per-connection token-bucket rate limiter for hub operation submissions. Sustained rate and burst
/// come from <see cref="CollaborationOptions"/>. Repeated violations escalate to a forced disconnect
/// so a flooding client is removed rather than merely throttled.
/// </summary>
public sealed class HubRateLimiter(IOptions<CollaborationOptions> options)
{
    private sealed class Bucket
    {
        public double Tokens;
        public long LastTimestamp;
        public int Violations;
    }

    private readonly CollaborationOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    public void Register(string connectionId) =>
        _buckets[connectionId] = new Bucket { Tokens = _options.OperationBurst, LastTimestamp = Stopwatch.GetTimestamp() };

    public void Remove(string connectionId) => _buckets.TryRemove(connectionId, out _);

    public RateDecision Acquire(string connectionId)
    {
        var bucket = _buckets.GetOrAdd(connectionId,
            _ => new Bucket { Tokens = _options.OperationBurst, LastTimestamp = Stopwatch.GetTimestamp() });

        lock (bucket)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsedSeconds = (now - bucket.LastTimestamp) / (double)Stopwatch.Frequency;
            bucket.LastTimestamp = now;
            bucket.Tokens = Math.Min(_options.OperationBurst, bucket.Tokens + (elapsedSeconds * _options.OperationsPerSecondPerConnection));

            if (bucket.Tokens >= 1)
            {
                bucket.Tokens -= 1;
                bucket.Violations = 0;
                return RateDecision.Allowed;
            }

            bucket.Violations++;
            return bucket.Violations >= _options.MaxViolationsBeforeDisconnect
                ? RateDecision.Disconnect
                : RateDecision.Throttled;
        }
    }
}
