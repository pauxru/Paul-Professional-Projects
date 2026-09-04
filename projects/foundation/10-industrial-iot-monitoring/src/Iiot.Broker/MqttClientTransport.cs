using System.Text;
using Iiot.Protocol;

namespace Iiot.Broker;

/// <summary>Adapter that makes the hand-written TCP MQTT client available through the transport port.</summary>
public sealed class MqttClientTransport : IMessageTransport
{
    private readonly MqttTcpClient _client;
    private readonly List<IAsyncDisposable> _subscriptions = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly List<(string Filter, Func<TransportMessage, Task> Handler)> _handlers = [];
    private Task? _dispatchLoop;

    private MqttClientTransport(MqttTcpClient client)
    {
        _client = client;
    }

    public static async Task<MqttClientTransport> ConnectAsync(
        string host,
        int port,
        MqttConnectPacket connect,
        CancellationToken cancellationToken = default)
    {
        var client = new MqttTcpClient();
        await client.ConnectAsync(host, port, connect, cancellationToken);
        var transport = new MqttClientTransport(client);
        transport._dispatchLoop = transport.DispatchAsync(transport._lifetime.Token);
        return transport;
    }

    public Task PublishAsync(TransportMessage message, CancellationToken cancellationToken = default) =>
        _client.PublishAsync(message.Topic, message.Payload, message.QualityOfService, message.Retain, cancellationToken);

    public async ValueTask<IAsyncDisposable> SubscribeAsync(
        string topicFilter,
        Func<TransportMessage, Task> handler,
        CancellationToken cancellationToken = default)
    {
        await _client.SubscribeAsync([new MqttSubscription(topicFilter, MqttQualityOfService.AtLeastOnce)], cancellationToken);
        var item = (topicFilter, handler);
        lock (_gate)
        {
            _handlers.Add(item);
        }

        var subscription = new HandlerSubscription(this, item);
        _subscriptions.Add(subscription);
        return subscription;
    }

    private async Task DispatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var publish = await _client.ReceivePublishAsync(cancellationToken);
                List<Func<TransportMessage, Task>> handlers;
                lock (_gate)
                {
                    handlers = _handlers
                        .Where(item => TopicFilters.Matches(item.Filter, publish.Topic))
                        .Select(item => item.Handler)
                        .ToList();
                }

                var message = new TransportMessage(publish.Topic, publish.Payload, publish.QualityOfService, publish.Retain);
                foreach (var handler in handlers)
                {
                    await handler(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal.
        }
    }

    private void Remove((string Filter, Func<TransportMessage, Task> Handler) item)
    {
        lock (_gate)
        {
            _handlers.Remove(item);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_dispatchLoop is not null)
        {
            await _dispatchLoop;
        }

        foreach (var subscription in _subscriptions)
        {
            await subscription.DisposeAsync();
        }

        await _client.DisposeAsync();
        _lifetime.Dispose();
    }

    private sealed class HandlerSubscription(
        MqttClientTransport owner,
        (string Filter, Func<TransportMessage, Task> Handler) item) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            owner.Remove(item);
            return ValueTask.CompletedTask;
        }
    }
}
