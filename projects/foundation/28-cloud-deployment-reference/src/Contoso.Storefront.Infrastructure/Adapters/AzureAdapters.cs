using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Contoso.Storefront.Application.Configuration;
using Contoso.Storefront.Application.Ports;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Contoso.Storefront.Infrastructure.Adapters;

public sealed class RedisCacheHealthProbe : ICacheHealthProbe, IAsyncDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>> _connection;

    public RedisCacheHealthProbe(IOptions<CacheOptions> options)
    {
        var connectionString = options.Value.ConnectionString
            ?? throw new InvalidOperationException(
                "Cache:ConnectionString is required for the Redis adapter.");
        _connection = new Lazy<Task<ConnectionMultiplexer>>(
            () => ConnectionMultiplexer.ConnectAsync(connectionString));
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = await _connection.Value.WaitAsync(cancellationToken);
            _ = await connection.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return connection.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection.IsValueCreated)
        {
            var connection = await _connection.Value;
            await connection.CloseAsync();
            connection.Dispose();
        }
    }
}

public sealed class AzureServiceBusAdapter :
    IOutboxTransport,
    IMessageBusHealthProbe,
    IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;
    private readonly ServiceBusReceiver _receiver;
    private readonly string _queueName;

    public AzureServiceBusAdapter(IOptions<MessagingOptions> options)
    {
        var settings = options.Value;
        _queueName = settings.QueueName;

        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            _client = new ServiceBusClient(settings.ConnectionString);
        }
        else
        {
            var fullyQualifiedNamespace = settings.FullyQualifiedNamespace
                ?? throw new InvalidOperationException(
                    "Messaging:FullyQualifiedNamespace is required for managed identity.");
            var credential = new DefaultAzureCredential();
            _client = new ServiceBusClient(fullyQualifiedNamespace, credential);
        }

        _sender = _client.CreateSender(_queueName);
        _receiver = _client.CreateReceiver(_queueName);
    }

    public Task PublishAsync(
        Guid messageId,
        string type,
        string payload,
        CancellationToken cancellationToken)
    {
        var message = new ServiceBusMessage(payload)
        {
            MessageId = messageId.ToString("N"),
            Subject = type,
            ContentType = "application/json"
        };
        return _sender.SendMessageAsync(message, cancellationToken);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await _receiver.PeekMessagesAsync(1, cancellationToken: cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _receiver.DisposeAsync();
        await _client.DisposeAsync();
    }
}
