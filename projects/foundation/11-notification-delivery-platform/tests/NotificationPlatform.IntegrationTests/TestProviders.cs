namespace NotificationPlatform.IntegrationTests;

using System.Threading;
using System.Threading.Tasks;
using NotificationPlatform.Application.Providers;
using NotificationPlatform.Domain.Common;

/// <summary>
/// Test providers with fully deterministic behavior. No RNG.
/// </summary>
public sealed class AlwaysFailProvider : IChannelProvider
{
    public AlwaysFailProvider(string name, NotificationChannel channel, ProviderResultKind kind, TimeSpan? retryAfter = null)
    {
        Name = name;
        Channel = channel;
        _kind = kind;
        _retryAfter = retryAfter;
    }

    private readonly ProviderResultKind _kind;
    private readonly TimeSpan? _retryAfter;

    public string Name { get; }
    public NotificationChannel Channel { get; }
    public int Calls;

    public Task<ProviderSendResult> SendAsync(ProviderSendRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new ProviderSendResult(_kind, null, 1, $"{Name}:forced_{_kind}", _retryAfter));
    }
}

public sealed class AlwaysSucceedProvider : IChannelProvider
{
    public AlwaysSucceedProvider(string name, NotificationChannel channel)
    {
        Name = name;
        Channel = channel;
    }

    public string Name { get; }
    public NotificationChannel Channel { get; }
    public int Calls;

    public Task<ProviderSendResult> SendAsync(ProviderSendRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new ProviderSendResult(ProviderResultKind.Success, $"{Name}-msg-{Calls}", 1, null, null));
    }
}
