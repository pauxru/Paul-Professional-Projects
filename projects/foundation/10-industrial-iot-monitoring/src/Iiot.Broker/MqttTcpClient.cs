using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using Iiot.Protocol;

namespace Iiot.Broker;

public sealed class MqttTcpClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _pingLock = new(1, 1);
    private readonly Channel<MqttPublishPacket> _publishes = Channel.CreateUnbounded<MqttPublishPacket>();
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<MqttPubAckPacket>> _publishAcks = new();
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<MqttSubAckPacket>> _subscriptionAcks = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _lifetime;
    private Task? _receiveLoop;
    private TaskCompletionSource<MqttPingRespPacket>? _pingResponse;
    private int _packetIdentifier;
    private int _disposed;

    public bool IsConnected => _client?.Connected == true;

    public async Task ConnectAsync(
        string host,
        int port,
        MqttConnectPacket connect,
        CancellationToken cancellationToken = default)
    {
        if (_client is not null)
        {
            throw new InvalidOperationException("The MQTT client is already connected.");
        }

        _client = new TcpClient();
        await _client.ConnectAsync(host, port, cancellationToken);
        _stream = _client.GetStream();
        await MqttCodec.WritePacketAsync(_stream, connect, cancellationToken);
        var response = await MqttCodec.ReadPacketAsync(_stream, cancellationToken) as MqttConnAckPacket
            ?? throw new MqttProtocolException("Broker did not send CONNACK.");
        if (response.ReturnCode != 0)
        {
            throw new MqttProtocolException($"Broker rejected CONNECT with code {response.ReturnCode}.");
        }

        _lifetime = new CancellationTokenSource();
        _receiveLoop = ReceiveLoopAsync(_lifetime.Token);
    }

    public async Task PublishAsync(
        string topic,
        byte[] payload,
        MqttQualityOfService qualityOfService = MqttQualityOfService.AtMostOnce,
        bool retain = false,
        CancellationToken cancellationToken = default)
    {
        ushort? packetIdentifier = qualityOfService == MqttQualityOfService.AtLeastOnce ? NextPacketIdentifier() : null;
        TaskCompletionSource<MqttPubAckPacket>? acknowledgement = null;
        if (packetIdentifier is { } identifier)
        {
            acknowledgement = new TaskCompletionSource<MqttPubAckPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_publishAcks.TryAdd(identifier, acknowledgement))
            {
                throw new InvalidOperationException("Packet identifier collision.");
            }
        }

        try
        {
            await SendAsync(new MqttPublishPacket(topic, payload, qualityOfService, retain, false, packetIdentifier), cancellationToken);
            if (acknowledgement is not null)
            {
                await acknowledgement.Task.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            if (packetIdentifier is { } cleanupIdentifier)
            {
                _publishAcks.TryRemove(cleanupIdentifier, out _);
            }
        }
    }

    public async Task SubscribeAsync(
        IReadOnlyList<MqttSubscription> subscriptions,
        CancellationToken cancellationToken = default)
    {
        var identifier = NextPacketIdentifier();
        var acknowledgement = new TaskCompletionSource<MqttSubAckPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_subscriptionAcks.TryAdd(identifier, acknowledgement))
        {
            throw new InvalidOperationException("Packet identifier collision.");
        }

        try
        {
            await SendAsync(new MqttSubscribePacket(identifier, subscriptions), cancellationToken);
            await acknowledgement.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _subscriptionAcks.TryRemove(identifier, out _);
        }
    }

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        await _pingLock.WaitAsync(cancellationToken);
        try
        {
            _pingResponse = new TaskCompletionSource<MqttPingRespPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            await SendAsync(new MqttPingReqPacket(), cancellationToken);
            await _pingResponse.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pingResponse = null;
            _pingLock.Release();
        }
    }

    public async Task<MqttPublishPacket> ReceivePublishAsync(CancellationToken cancellationToken = default)
    {
        var publish = await _publishes.Reader.ReadAsync(cancellationToken);
        if (publish.QualityOfService == MqttQualityOfService.AtLeastOnce)
        {
            await SendAsync(new MqttPubAckPacket(publish.PacketIdentifier!.Value), cancellationToken);
        }

        return publish;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_stream is not null)
        {
            await SendAsync(new MqttDisconnectPacket(), cancellationToken);
        }

        await DisposeAsync();
    }

    private async Task SendAsync(MqttPacket packet, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("The MQTT client is not connected.");
        }

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await MqttCodec.WritePacketAsync(_stream, packet, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        Exception? completionError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested && _stream is not null)
            {
                var packet = await MqttCodec.ReadPacketAsync(_stream, cancellationToken);
                switch (packet)
                {
                    case MqttPublishPacket publish:
                        await _publishes.Writer.WriteAsync(publish, cancellationToken);
                        break;
                    case MqttPubAckPacket pubAck:
                        if (_publishAcks.TryGetValue(pubAck.PacketIdentifier, out var publishAck))
                        {
                            publishAck.TrySetResult(pubAck);
                        }

                        break;
                    case MqttSubAckPacket subAck:
                        if (_subscriptionAcks.TryGetValue(subAck.PacketIdentifier, out var subscriptionAck))
                        {
                            subscriptionAck.TrySetResult(subAck);
                        }

                        break;
                    case MqttPingRespPacket ping:
                        _pingResponse?.TrySetResult(ping);
                        break;
                    default:
                        throw new MqttProtocolException($"Unexpected broker packet {packet.Type}.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disposal.
        }
        catch (Exception exception)
        {
            completionError = exception;
        }
        finally
        {
            _publishes.Writer.TryComplete(completionError);
            foreach (var pending in _publishAcks.Values)
            {
                pending.TrySetException(completionError ?? new EndOfStreamException("MQTT connection closed."));
            }

            foreach (var pending in _subscriptionAcks.Values)
            {
                pending.TrySetException(completionError ?? new EndOfStreamException("MQTT connection closed."));
            }

            _pingResponse?.TrySetException(completionError ?? new EndOfStreamException("MQTT connection closed."));
        }
    }

    private ushort NextPacketIdentifier()
    {
        var next = Interlocked.Increment(ref _packetIdentifier) % ushort.MaxValue;
        return (ushort)(next == 0 ? 1 : next);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime?.Cancel();
        _client?.Close();
        if (_receiveLoop is not null)
        {
            await _receiveLoop;
        }

        _lifetime?.Dispose();
        _sendLock.Dispose();
        _pingLock.Dispose();
    }
}
