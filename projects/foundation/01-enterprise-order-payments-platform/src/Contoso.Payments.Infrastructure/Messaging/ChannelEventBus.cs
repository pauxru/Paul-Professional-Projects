using System.Threading.Channels;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Contoso.Payments.Application.Abstractions;

namespace Contoso.Payments.Infrastructure.Messaging;

/// <summary>
/// The default in-process bus.  A bounded <see cref="Channel{T}"/> holds published events, and
/// a background <see cref="IHostedService"/> pumps them to subscribers.  This project deliberately
/// keeps the bus tiny — the "real" adapters (RabbitMQ, Azure Service Bus) are declared as
/// documented stubs elsewhere so the composition root shows the switch.
/// </summary>
public sealed class ChannelEventBus : IEventBus
{
    private readonly Channel<BusEnvelope> _channel;
    private readonly ILogger<ChannelEventBus> _log;

    public ChannelEventBus(ILogger<ChannelEventBus> log)
    {
        _log = log;
        _channel = Channel.CreateBounded<BusEnvelope>(new BoundedChannelOptions(1024)
        {
            SingleWriter = false,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    internal ChannelReader<BusEnvelope> Reader => _channel.Reader;

    public ValueTask PublishAsync(string topic, string payloadJson, CancellationToken ct)
    {
        _log.LogDebug("Publishing bus event {Topic}", topic);
        return _channel.Writer.WriteAsync(new BusEnvelope(topic, payloadJson), ct);
    }

    public void Complete() => _channel.Writer.TryComplete();
}

public sealed record BusEnvelope(string Topic, string PayloadJson);

public sealed class ChannelEventBusPump : BackgroundService
{
    private readonly ChannelEventBus _bus;
    private readonly ILogger<ChannelEventBusPump> _log;

    public ChannelEventBusPump(IEventBus bus, ILogger<ChannelEventBusPump> log)
    {
        _bus = (ChannelEventBus)bus;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var env in _bus.Reader.ReadAllAsync(stoppingToken))
            {
                // In this reference implementation, subscribers are logged.  A real subscriber
                // would fan out to projections / notification services.
                _log.LogInformation("Bus event delivered {Topic}", env.Topic);
            }
        }
        catch (OperationCanceledException) { }
    }
}
