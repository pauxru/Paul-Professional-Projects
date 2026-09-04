namespace NotificationPlatform.Infrastructure.Providers.Webhook;

using NotificationPlatform.Domain.Common;
using NotificationPlatform.Infrastructure.Providers.Common;

public sealed class HttpWebhookSimulator : SimulatedChannelProviderBase
{
    public HttpWebhookSimulator(int seed, IProviderClock clock)
        : base(new ProviderBehaviorProfile
        {
            Name = "HttpWebhookSimulator",
            Channel = NotificationChannel.Webhook,
            BaseLatencyMs = 6,
            JitterLatencyMs = 8,
            TransientFailureRate = 0.05,
            PermanentFailureRate = 0.03,
            ThrottleRate = 0.02,
            ThrottleRetryAfter = TimeSpan.FromSeconds(1),
        }, seed, clock)
    { }
}

public sealed class ServiceBusWebhookSimulator : SimulatedChannelProviderBase
{
    public ServiceBusWebhookSimulator(int seed, IProviderClock clock)
        : base(new ProviderBehaviorProfile
        {
            Name = "ServiceBusWebhookSimulator",
            Channel = NotificationChannel.Webhook,
            BaseLatencyMs = 3,
            JitterLatencyMs = 4,
            TransientFailureRate = 0.02,
            PermanentFailureRate = 0.01,
            ThrottleRate = 0.01,
            ThrottleRetryAfter = TimeSpan.FromSeconds(1),
        }, seed, clock)
    { }
}
