namespace NotificationPlatform.Infrastructure.Providers.Common;

using System.Threading;
using NotificationPlatform.Application.Providers;
using NotificationPlatform.Domain.Common;

public sealed class ProviderBehaviorProfile
{
    public string Name { get; init; } = string.Empty;
    public NotificationChannel Channel { get; init; }
    public int BaseLatencyMs { get; init; } = 5;
    public int JitterLatencyMs { get; init; } = 5;
    public double TransientFailureRate { get; init; } = 0.0;
    public double PermanentFailureRate { get; init; } = 0.0;
    public double ThrottleRate { get; init; } = 0.0;
    public TimeSpan ThrottleRetryAfter { get; init; } = TimeSpan.FromSeconds(1);
}

public abstract class SimulatedChannelProviderBase : IChannelProvider
{
    private readonly Random _rng;
    private readonly object _sync = new();
    protected readonly IProviderClock Clock;

    protected SimulatedChannelProviderBase(ProviderBehaviorProfile profile, int seed, IProviderClock clock)
    {
        Profile = profile;
        _rng = new Random(seed);
        Clock = clock;
    }

    public ProviderBehaviorProfile Profile { get; }

    public string Name => Profile.Name;
    public NotificationChannel Channel => Profile.Channel;

    protected int NextInt(int min, int max)
    {
        lock (_sync) return _rng.Next(min, max);
    }

    protected double NextDouble()
    {
        lock (_sync) return _rng.NextDouble();
    }

    public virtual async Task<ProviderSendResult> SendAsync(ProviderSendRequest request, CancellationToken cancellationToken)
    {
        var latencyMs = Profile.BaseLatencyMs + (Profile.JitterLatencyMs == 0 ? 0 : NextInt(0, Profile.JitterLatencyMs));
        await Task.Delay(Math.Min(latencyMs, 5), cancellationToken).ConfigureAwait(false); // physically don't stall CI for long

        var roll = NextDouble();
        double cursor = Profile.PermanentFailureRate;
        if (roll < cursor)
            return new ProviderSendResult(ProviderResultKind.PermanentFailure, null, latencyMs, $"{Name}:permanent_failure", null);
        cursor += Profile.TransientFailureRate;
        if (roll < cursor)
            return new ProviderSendResult(ProviderResultKind.TransientFailure, null, latencyMs, $"{Name}:transient", null);
        cursor += Profile.ThrottleRate;
        if (roll < cursor)
            return new ProviderSendResult(ProviderResultKind.Throttled, null, latencyMs, $"{Name}:429", Profile.ThrottleRetryAfter);

        var messageId = $"{Name}-{Clock.UtcNow.ToUnixTimeMilliseconds():x}-{NextInt(1000, 9999)}";
        return new ProviderSendResult(ProviderResultKind.Success, messageId, latencyMs, null, null);
    }
}

public interface IProviderClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class ProviderClock : IProviderClock
{
    private readonly NotificationPlatform.Application.Abstractions.IClock _clock;
    public ProviderClock(NotificationPlatform.Application.Abstractions.IClock clock) => _clock = clock;
    public DateTimeOffset UtcNow => _clock.UtcNow;
}
