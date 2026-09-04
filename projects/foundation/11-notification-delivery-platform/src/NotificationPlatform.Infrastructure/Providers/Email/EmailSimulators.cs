namespace NotificationPlatform.Infrastructure.Providers.Email;

using NotificationPlatform.Domain.Common;
using NotificationPlatform.Infrastructure.Providers.Common;

public sealed class SmtpSimulator : SimulatedChannelProviderBase
{
    public SmtpSimulator(int seed, IProviderClock clock)
        : base(new ProviderBehaviorProfile
        {
            Name = "SmtpSimulator",
            Channel = NotificationChannel.Email,
            BaseLatencyMs = 8,
            JitterLatencyMs = 12,
            TransientFailureRate = 0.10,
            PermanentFailureRate = 0.02,
            ThrottleRate = 0.03,
            ThrottleRetryAfter = TimeSpan.FromSeconds(2),
        }, seed, clock)
    { }
}

public sealed class SendGridSimulator : SimulatedChannelProviderBase
{
    public SendGridSimulator(int seed, IProviderClock clock)
        : base(new ProviderBehaviorProfile
        {
            Name = "SendGridSimulator",
            Channel = NotificationChannel.Email,
            BaseLatencyMs = 4,
            JitterLatencyMs = 6,
            TransientFailureRate = 0.03,
            PermanentFailureRate = 0.01,
            ThrottleRate = 0.01,
            ThrottleRetryAfter = TimeSpan.FromSeconds(1),
        }, seed, clock)
    { }
}
