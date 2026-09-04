namespace NotificationPlatform.Infrastructure.Providers.Sms;

using NotificationPlatform.Domain.Common;
using NotificationPlatform.Infrastructure.Providers.Common;

public sealed class TwilioSimulator : SimulatedChannelProviderBase
{
    public TwilioSimulator(int seed, IProviderClock clock)
        : base(new ProviderBehaviorProfile
        {
            Name = "TwilioSimulator",
            Channel = NotificationChannel.Sms,
            BaseLatencyMs = 6,
            JitterLatencyMs = 8,
            TransientFailureRate = 0.05,
            PermanentFailureRate = 0.02,
            ThrottleRate = 0.02,
            ThrottleRetryAfter = TimeSpan.FromSeconds(1),
        }, seed, clock)
    { }
}

public sealed class AfricasTalkingSimulator : SimulatedChannelProviderBase
{
    public AfricasTalkingSimulator(int seed, IProviderClock clock)
        : base(new ProviderBehaviorProfile
        {
            Name = "AfricasTalkingSimulator",
            Channel = NotificationChannel.Sms,
            BaseLatencyMs = 4,
            JitterLatencyMs = 4,
            TransientFailureRate = 0.03,
            PermanentFailureRate = 0.01,
            ThrottleRate = 0.01,
            ThrottleRetryAfter = TimeSpan.FromSeconds(1),
        }, seed, clock)
    { }
}
