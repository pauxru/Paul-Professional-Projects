using System.Text;
using Iiot.Broker;
using Iiot.Protocol;

namespace Iiot.UnitTests;

public sealed class BrokerTests
{
    [Fact]
    public async Task TcpBroker_ConnectAndPing_ReturnsPingResponse()
    {
        await using var broker = await StartBrokerAsync();
        await using var client = new MqttTcpClient();
        using var timeout = Timeout();

        await client.ConnectAsync("127.0.0.1", broker.Port, new MqttConnectPacket("ping-client"), timeout.Token);
        await client.PingAsync(timeout.Token);

        Assert.True(client.IsConnected);
        await client.DisconnectAsync(timeout.Token);
    }

    [Fact]
    public async Task TcpBroker_SubscribeAndPublish_DeliversMatchingMessage()
    {
        await using var broker = await StartBrokerAsync();
        await using var subscriber = new MqttTcpClient();
        await using var publisher = new MqttTcpClient();
        using var timeout = Timeout();

        await subscriber.ConnectAsync("127.0.0.1", broker.Port, new MqttConnectPacket("subscriber"), timeout.Token);
        await subscriber.SubscribeAsync([new MqttSubscription("plants/+/devices/+/telemetry", MqttQualityOfService.AtLeastOnce)], timeout.Token);
        await publisher.ConnectAsync("127.0.0.1", broker.Port, new MqttConnectPacket("publisher"), timeout.Token);
        await publisher.PublishAsync("plants/a/devices/cmp-01/telemetry", Encoding.UTF8.GetBytes("{\"sequence\":1}"), MqttQualityOfService.AtMostOnce, cancellationToken: timeout.Token);

        var delivered = await subscriber.ReceivePublishAsync(timeout.Token);

        Assert.Equal("plants/a/devices/cmp-01/telemetry", delivered.Topic);
        Assert.Equal("{\"sequence\":1}", Encoding.UTF8.GetString(delivered.Payload));
        await subscriber.DisconnectAsync(timeout.Token);
        await publisher.DisconnectAsync(timeout.Token);
    }

    [Fact]
    public async Task TcpBroker_RetainedPublish_ReplaysToFutureSubscription()
    {
        await using var broker = await StartBrokerAsync();
        await using var publisher = new MqttTcpClient();
        using var timeout = Timeout();

        await publisher.ConnectAsync("127.0.0.1", broker.Port, new MqttConnectPacket("retainer"), timeout.Token);
        await publisher.PublishAsync("plants/a/devices/cmp-01/status", Encoding.UTF8.GetBytes("online"), retain: true, cancellationToken: timeout.Token);
        await publisher.DisconnectAsync(timeout.Token);

        await using var subscriber = new MqttTcpClient();
        await subscriber.ConnectAsync("127.0.0.1", broker.Port, new MqttConnectPacket("late-subscriber"), timeout.Token);
        await subscriber.SubscribeAsync([new MqttSubscription("plants/#", MqttQualityOfService.AtMostOnce)], timeout.Token);
        var retained = await subscriber.ReceivePublishAsync(timeout.Token);

        Assert.True(retained.Retain);
        Assert.Equal("online", Encoding.UTF8.GetString(retained.Payload));
        await subscriber.DisconnectAsync(timeout.Token);
    }

    [Fact]
    public async Task TcpBroker_QosOnePublish_AcknowledgesPublisherAndDeliversQosOne()
    {
        await using var broker = await StartBrokerAsync();
        await using var subscriber = new MqttTcpClient();
        await using var publisher = new MqttTcpClient();
        using var timeout = Timeout();

        await subscriber.ConnectAsync("127.0.0.1", broker.Port, new MqttConnectPacket("qos-subscriber"), timeout.Token);
        await subscriber.SubscribeAsync([new MqttSubscription("telemetry/#", MqttQualityOfService.AtLeastOnce)], timeout.Token);
        await publisher.ConnectAsync("127.0.0.1", broker.Port, new MqttConnectPacket("qos-publisher"), timeout.Token);

        await publisher.PublishAsync("telemetry/cmp-01", [1, 2, 3], MqttQualityOfService.AtLeastOnce, cancellationToken: timeout.Token);
        var delivered = await subscriber.ReceivePublishAsync(timeout.Token);

        Assert.Equal(MqttQualityOfService.AtLeastOnce, delivered.QualityOfService);
        Assert.NotNull(delivered.PacketIdentifier);
        await subscriber.DisconnectAsync(timeout.Token);
        await publisher.DisconnectAsync(timeout.Token);
    }

    [Fact]
    public async Task TcpBroker_UngracefulDisconnect_PublishesLastWill()
    {
        await using var broker = await StartBrokerAsync();
        await using var watcher = new MqttTcpClient();
        using var timeout = Timeout();
        await watcher.ConnectAsync("127.0.0.1", broker.Port, new MqttConnectPacket("will-watcher"), timeout.Token);
        await watcher.SubscribeAsync([new MqttSubscription("status/#", MqttQualityOfService.AtMostOnce)], timeout.Token);

        var dyingClient = new MqttTcpClient();
        await dyingClient.ConnectAsync(
            "127.0.0.1",
            broker.Port,
            new MqttConnectPacket("will-client", LastWill: new MqttLastWill("status/will-client", Encoding.UTF8.GetBytes("offline"))),
            timeout.Token);
        await dyingClient.DisposeAsync();

        var will = await watcher.ReceivePublishAsync(timeout.Token);
        Assert.Equal("status/will-client", will.Topic);
        Assert.Equal("offline", Encoding.UTF8.GetString(will.Payload));
        await watcher.DisconnectAsync(timeout.Token);
    }

    [Fact]
    public async Task TcpBroker_RejectsUnauthenticatedConnect()
    {
        await using var broker = new MqttBroker(new MqttBrokerOptions
        {
            Port = 0,
            Authenticate = packet => packet.Username == "approved"
        });
        await broker.StartAsync();
        await using var client = new MqttTcpClient();
        using var timeout = Timeout();

        var exception = await Assert.ThrowsAsync<MqttProtocolException>(() =>
            client.ConnectAsync("127.0.0.1", broker.Port, new MqttConnectPacket("rejected", Username: "wrong"), timeout.Token));

        Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<MqttBroker> StartBrokerAsync()
    {
        var broker = new MqttBroker(new MqttBrokerOptions { Port = 0 });
        await broker.StartAsync();
        return broker;
    }

    private static CancellationTokenSource Timeout() => new(TimeSpan.FromSeconds(10));
}
