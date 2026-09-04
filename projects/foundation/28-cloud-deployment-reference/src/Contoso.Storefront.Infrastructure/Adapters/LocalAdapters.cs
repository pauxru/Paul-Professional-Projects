using System.Collections.Concurrent;
using Contoso.Storefront.Application.Configuration;
using Contoso.Storefront.Application.Ports;
using Contoso.Storefront.Application.Release;
using Microsoft.Extensions.Options;

namespace Contoso.Storefront.Infrastructure.Adapters;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class GuidIdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.NewGuid();
}

public sealed class SystemAsyncDelay : IAsyncDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public sealed class OptionsFeatureFlagProvider(IOptionsMonitor<ReleaseOptions> options) : IFeatureFlagProvider
{
    public bool IsEnabled(string flagName) =>
        flagName == FeatureFlags.NewPricingDarkLaunch && options.CurrentValue.NewPricingDarkLaunch;
}

public sealed class InMemoryMessageBus : IOutboxTransport, IMessageBusHealthProbe
{
    private readonly ConcurrentQueue<(string Type, string Payload)> _messages = new();

    public bool IsHealthy { get; set; } = true;
    public IReadOnlyCollection<(string Type, string Payload)> Published => _messages.ToArray();

    public Task PublishAsync(
        Guid messageId,
        string type,
        string payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsHealthy)
        {
            throw new InvalidOperationException("The in-memory message bus is faulted.");
        }

        _messages.Enqueue((type, payload));
        return Task.CompletedTask;
    }

    Task<bool> IMessageBusHealthProbe.IsHealthyAsync(CancellationToken cancellationToken) =>
        Task.FromResult(IsHealthy);
}

public sealed class MemoryCacheHealthProbe : ICacheHealthProbe
{
    public bool IsHealthy { get; set; } = true;

    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) =>
        Task.FromResult(IsHealthy);
}

public sealed class AsyncLocalCorrelationContext : ICorrelationContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public string? CorrelationId
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}
