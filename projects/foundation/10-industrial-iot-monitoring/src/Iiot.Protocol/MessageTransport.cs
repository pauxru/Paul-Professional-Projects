namespace Iiot.Protocol;

public sealed record TransportMessage(
    string Topic,
    byte[] Payload,
    MqttQualityOfService QualityOfService = MqttQualityOfService.AtMostOnce,
    bool Retain = false);

public interface IMessageTransport : IAsyncDisposable
{
    Task PublishAsync(TransportMessage message, CancellationToken cancellationToken = default);

    ValueTask<IAsyncDisposable> SubscribeAsync(
        string topicFilter,
        Func<TransportMessage, Task> handler,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryMessageTransport : IMessageTransport
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly Dictionary<string, TransportMessage> _retained = new(StringComparer.Ordinal);

    public async Task PublishAsync(TransportMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!TopicFilters.IsValidTopicName(message.Topic))
        {
            throw new ArgumentException("A concrete MQTT topic name is required.", nameof(message));
        }

        List<Func<TransportMessage, Task>> handlers;
        lock (_gate)
        {
            if (message.Retain)
            {
                if (message.Payload.Length == 0)
                {
                    _retained.Remove(message.Topic);
                }
                else
                {
                    _retained[message.Topic] = message;
                }
            }

            handlers = _subscriptions
                .Where(subscription => TopicFilters.Matches(subscription.Filter, message.Topic))
                .Select(subscription => subscription.Handler)
                .ToList();
        }

        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await handler(message);
        }
    }

    public async ValueTask<IAsyncDisposable> SubscribeAsync(
        string topicFilter,
        Func<TransportMessage, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicFilter);
        ArgumentNullException.ThrowIfNull(handler);
        if (!TopicFilters.IsValidFilter(topicFilter))
        {
            throw new ArgumentException("Invalid MQTT topic filter.", nameof(topicFilter));
        }

        var subscription = new Subscription(topicFilter, handler);
        List<TransportMessage> retained;
        lock (_gate)
        {
            _subscriptions.Add(subscription);
            retained = _retained.Values.Where(message => TopicFilters.Matches(topicFilter, message.Topic)).ToList();
        }

        foreach (var message in retained)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await handler(message);
        }

        return new Unsubscriber(this, subscription);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _subscriptions.Clear();
            _retained.Clear();
        }

        return ValueTask.CompletedTask;
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private sealed record Subscription(string Filter, Func<TransportMessage, Task> Handler);

    private sealed class Unsubscriber(InMemoryMessageTransport owner, Subscription subscription) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            owner.Remove(subscription);
            return ValueTask.CompletedTask;
        }
    }
}
