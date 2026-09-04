namespace NotificationPlatform.Infrastructure.Providers.Push;

using NotificationPlatform.Domain.Common;
using NotificationPlatform.Infrastructure.Providers.Common;

public sealed class FcmSimulator : SimulatedChannelProviderBase
{
    public FcmSimulator(int seed, IProviderClock clock)
        : base(new ProviderBehaviorProfile
        {
            Name = "FcmSimulator",
            Channel = NotificationChannel.Push,
            BaseLatencyMs = 5,
            JitterLatencyMs = 5,
            TransientFailureRate = 0.04,
            PermanentFailureRate = 0.02,
            ThrottleRate = 0.02,
            ThrottleRetryAfter = TimeSpan.FromSeconds(1),
        }, seed, clock)
    { }
}

public sealed class ApnsSimulator : SimulatedChannelProviderBase
{
    public ApnsSimulator(int seed, IProviderClock clock)
        : base(new ProviderBehaviorProfile
        {
            Name = "ApnsSimulator",
            Channel = NotificationChannel.Push,
            BaseLatencyMs = 4,
            JitterLatencyMs = 4,
            TransientFailureRate = 0.03,
            PermanentFailureRate = 0.02,
            ThrottleRate = 0.02,
            ThrottleRetryAfter = TimeSpan.FromSeconds(1),
        }, seed, clock)
    { }
}
