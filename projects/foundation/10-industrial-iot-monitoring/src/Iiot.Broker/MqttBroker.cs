using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Iiot.Protocol;

namespace Iiot.Broker;

public sealed class MqttBrokerOptions
{
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;
    public int Port { get; init; } = 1883;
    public Func<MqttConnectPacket, bool>? Authenticate { get; init; }
}

public sealed class MqttBroker : IAsyncDisposable
{
    private readonly MqttBrokerOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, BrokerSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MqttPublishPacket> _retained = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, ClientConnection> _connections = new();
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public MqttBroker(MqttBrokerOptions? options = null)
    {
        _options = options ?? new MqttBrokerOptions();
    }

    public int Port => ((IPEndPoint?)_listener?.LocalEndpoint)?.Port ?? _options.Port;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        _listener = new TcpListener(_options.BindAddress, _options.Port);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_stop.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        _listener?.Stop();
        foreach (var connection in _connections.Values)
        {
            connection.SuppressLastWill();
            connection.Close();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected while stopping the locally hosted broker.
            }
        }

        _listener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                _ = HandleConnectionAsync(new ClientConnection(client), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HandleConnectionAsync(ClientConnection connection, CancellationToken brokerCancellationToken)
    {
        _connections[connection.Id] = connection;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, brokerCancellationToken);
            var connect = await MqttCodec.ReadPacketAsync(connection.Stream, linked.Token) as MqttConnectPacket
                ?? throw new MqttProtocolException("The first MQTT packet must be CONNECT.");
            if (_options.Authenticate is not null && !_options.Authenticate(connect))
            {
                await connection.SendAsync(new MqttConnAckPacket(false, 5), linked.Token);
                return;
            }

            connection.ClientId = connect.ClientId;
            connection.LastWill = connect.LastWill;
            connection.CleanSession = connect.CleanSession;
            var sessionPresent = AttachSession(connection, connect.CleanSession);
            await connection.SendAsync(new MqttConnAckPacket(sessionPresent, 0), linked.Token);

            while (!linked.IsCancellationRequested)
            {
                var packet = await MqttCodec.ReadPacketAsync(connection.Stream, linked.Token);
                switch (packet)
                {
                    case MqttPublishPacket publish:
                        await PublishAsync(publish, linked.Token);
                        if (publish.QualityOfService == MqttQualityOfService.AtLeastOnce)
                        {
                            await connection.SendAsync(new MqttPubAckPacket(publish.PacketIdentifier!.Value), linked.Token);
                        }

                        break;
                    case MqttSubscribePacket subscribe:
                        await SubscribeAsync(connection, subscribe, linked.Token);
                        break;
                    case MqttPingReqPacket:
                        await connection.SendAsync(new MqttPingRespPacket(), linked.Token);
                        break;
                    case MqttDisconnectPacket:
                        connection.SuppressLastWill();
                        return;
                    case MqttPubAckPacket:
                        break;
                    default:
                        throw new MqttProtocolException($"Packet {packet.Type} is not valid in a connected client flow.");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            connection.SuppressLastWill();
        }
        catch (EndOfStreamException)
        {
            // An ungraceful close is precisely when a last-will message is needed.
        }
        catch (IOException)
        {
            // Network close follows the same last-will path.
        }
        finally
        {
            DetachSession(connection);
            _connections.TryRemove(connection.Id, out _);
            if (connection.ShouldPublishLastWill && connection.LastWill is { } will)
            {
                try
                {
                    await PublishAsync(
                        new MqttPublishPacket(will.Topic, will.Payload, will.QualityOfService, will.Retain),
                        CancellationToken.None);
                }
                catch
                {
                    // The peer is gone and no broker caller can recover a best-effort LWT delivery.
                }
            }

            connection.Close();
        }
    }

    private bool AttachSession(ClientConnection connection, bool cleanSession)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(connection.ClientId!, out var previous))
            {
                previous.Connection?.SuppressLastWill();
                previous.Connection?.Close();
                var sessionPresent = !cleanSession && !previous.CleanSession && previous.Subscriptions.Count > 0;
                if (cleanSession || previous.CleanSession)
                {
                    _sessions[connection.ClientId!] = new BrokerSession(connection.ClientId!, connection, cleanSession);
                }
                else
                {
                    previous.Connection = connection;
                }

                return sessionPresent;
            }

            _sessions.Add(connection.ClientId!, new BrokerSession(connection.ClientId!, connection, cleanSession));
            return false;
        }
    }

    private void DetachSession(ClientConnection connection)
    {
        if (connection.ClientId is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_sessions.TryGetValue(connection.ClientId, out var session) && session.Connection == connection)
            {
                if (connection.CleanSession)
                {
                    _sessions.Remove(connection.ClientId);
                }
                else
                {
                    session.Connection = null;
                }
            }
        }
    }

    private async Task SubscribeAsync(ClientConnection connection, MqttSubscribePacket subscribe, CancellationToken cancellationToken)
    {
        List<(MqttPublishPacket Message, MqttQualityOfService SubscriptionQos)> retained;
        lock (_gate)
        {
            var session = _sessions[connection.ClientId!];
            foreach (var subscription in subscribe.Subscriptions)
            {
                session.Subscriptions.RemoveAll(item => item.Filter == subscription.TopicFilter);
                session.Subscriptions.Add(new Subscription(subscription.TopicFilter, subscription.QualityOfService));
            }

            retained = _retained.Values
                .SelectMany(message => subscribe.Subscriptions
                    .Where(subscription => TopicFilters.Matches(subscription.TopicFilter, message.Topic))
                    .Select(subscription => (message with { Retain = true }, subscription.QualityOfService)))
                .ToList();
        }

        await connection.SendAsync(
            new MqttSubAckPacket(subscribe.PacketIdentifier, subscribe.Subscriptions.Select(item => (byte)item.QualityOfService).ToArray()),
            cancellationToken);
        foreach (var (message, subscriptionQos) in retained)
        {
            var qos = (MqttQualityOfService)Math.Min((byte)message.QualityOfService, (byte)subscriptionQos);
            await connection.SendAsync(ForSubscription(connection, message, qos), cancellationToken);
        }
    }

    private async Task PublishAsync(MqttPublishPacket published, CancellationToken cancellationToken)
    {
        List<(ClientConnection Connection, MqttQualityOfService QualityOfService)> recipients;
        lock (_gate)
        {
            if (published.Retain)
            {
                if (published.Payload.Length == 0)
                {
                    _retained.Remove(published.Topic);
                }
                else
                {
                    _retained[published.Topic] = published;
                }
            }

            recipients = _sessions.Values
                .Where(session => session.Connection is not null)
                .Select(session => new
                {
                    Connection = session.Connection!,
                    Matching = session.Subscriptions
                        .Where(subscription => TopicFilters.Matches(subscription.Filter, published.Topic))
                        .Select(subscription => subscription.QualityOfService)
                        .ToArray()
                })
                .Where(item => item.Matching.Length > 0)
                .Select(item => (item.Connection, item.Matching.Max()))
                .ToList();
        }

        foreach (var (connection, requestedQos) in recipients)
        {
            var qos = (MqttQualityOfService)Math.Min((byte)published.QualityOfService, (byte)requestedQos);
            await connection.SendAsync(ForSubscription(connection, published with { Retain = false }, qos), cancellationToken);
        }
    }

    private static MqttPublishPacket ForSubscription(ClientConnection connection, MqttPublishPacket publish, MqttQualityOfService qos) =>
        publish with
        {
            QualityOfService = qos,
            PacketIdentifier = qos == MqttQualityOfService.AtLeastOnce ? connection.NextPacketIdentifier() : null
        };

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stop.Dispose();
    }

    private sealed class BrokerSession(string clientId, ClientConnection? connection, bool cleanSession)
    {
        public string ClientId { get; } = clientId;
        public ClientConnection? Connection { get; set; } = connection;
        public bool CleanSession { get; } = cleanSession;
        public List<Subscription> Subscriptions { get; } = [];
    }

    private sealed record Subscription(string Filter, MqttQualityOfService QualityOfService);

    private sealed class ClientConnection(TcpClient client)
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _packetIdentifier;
        private bool _lastWillSuppressed;

        public Guid Id { get; } = Guid.NewGuid();
        public NetworkStream Stream { get; } = client.GetStream();
        public string? ClientId { get; set; }
        public MqttLastWill? LastWill { get; set; }
        public bool CleanSession { get; set; }
        public bool ShouldPublishLastWill => !_lastWillSuppressed;

        public async Task SendAsync(MqttPacket packet, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await MqttCodec.WritePacketAsync(Stream, packet, cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public ushort NextPacketIdentifier()
        {
            var next = Interlocked.Increment(ref _packetIdentifier) % ushort.MaxValue;
            return (ushort)(next == 0 ? 1 : next);
        }

        public void SuppressLastWill() => _lastWillSuppressed = true;
        public void Close()
        {
            try
            {
                client.Close();
            }
            catch (ObjectDisposedException)
            {
                // Closing twice is harmless during shutdown.
            }
        }
    }
}
