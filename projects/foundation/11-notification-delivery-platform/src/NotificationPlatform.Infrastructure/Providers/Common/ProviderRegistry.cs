namespace NotificationPlatform.Infrastructure.Providers.Common;

using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Options;
using NotificationPlatform.Application.Abstractions;
using NotificationPlatform.Application.Options;
using NotificationPlatform.Application.Providers;
using NotificationPlatform.Domain.Common;

public interface IProviderRegistry
{
    IReadOnlyList<IChannelProvider> ProvidersFor(NotificationChannel channel);
    IReadOnlyDictionary<string, IChannelProvider> All();
}

public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<NotificationChannel, List<IChannelProvider>> _byChannel;
    private readonly Dictionary<string, IChannelProvider> _byName;

    public ProviderRegistry(IEnumerable<IChannelProvider> providers)
    {
        _byChannel = providers
            .GroupBy(p => p.Channel)
            .ToDictionary(g => g.Key, g => g.ToList());
        _byName = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IChannelProvider> ProvidersFor(NotificationChannel channel)
        => _byChannel.TryGetValue(channel, out var list) ? list : Array.Empty<IChannelProvider>();

    public IReadOnlyDictionary<string, IChannelProvider> All() => _byName;
}

public interface IProviderHealthTracker
{
    Task<CircuitState> GetStateAsync(string providerName, CancellationToken ct);
    Task RecordSuccessAsync(string providerName, NotificationChannel channel, CancellationToken ct);
    Task RecordFailureAsync(string providerName, NotificationChannel channel, DateTimeOffset now, CancellationToken ct);
    Task<bool> ProbeAndMaybeHalfOpenAsync(string providerName, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<NotificationPlatform.Domain.Providers.ProviderHealth>> ListAsync(CancellationToken ct);
}

public interface IProviderRateLimiter
{
    /// <summary>Blocks (asynchronously) if the caller would exceed the provider's per-second rate.</summary>
    Task AcquireAsync(string providerName, CancellationToken ct);
    /// <summary>Attempts to acquire a slot without blocking.</summary>
    bool TryAcquire(string providerName);
}

public sealed class InMemoryProviderRateLimiter : IProviderRateLimiter
{
    private readonly int _perSecond;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, LimiterState> _states = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryProviderRateLimiter(IOptions<NotificationOptions> options, IClock clock)
    {
        _perSecond = options.Value.PerProviderRatePerSecond;
        _clock = clock;
    }

    public bool TryAcquire(string providerName)
    {
        var state = _states.GetOrAdd(providerName, _ => new LimiterState());
        lock (state)
        {
            var now = _clock.UtcNow;
            var slot = (long)Math.Floor((now - DateTimeOffset.UnixEpoch).TotalSeconds);
            if (slot != state.CurrentSlot)
            {
                state.CurrentSlot = slot;
                state.Count = 0;
            }
            if (state.Count >= _perSecond) return false;
            state.Count++;
            return true;
        }
    }

    public async Task AcquireAsync(string providerName, CancellationToken ct)
    {
        while (!TryAcquire(providerName))
        {
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    private sealed class LimiterState
    {
        public long CurrentSlot;
        public int Count;
    }
}
