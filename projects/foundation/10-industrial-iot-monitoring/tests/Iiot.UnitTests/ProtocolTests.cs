using System.Buffers;
using System.Text;
using Iiot.Protocol;

namespace Iiot.UnitTests;

public sealed class ProtocolTests
{
    [Fact]
    public void EncodeDecode_Connect_WithCredentialsAndWill_RoundTrips()
    {
        var input = new MqttConnectPacket(
            "compressor-01",
            false,
            45,
            "device-user",
            Encoding.UTF8.GetBytes("device-key"),
            new MqttLastWill("plant/a/status", Encoding.UTF8.GetBytes("offline"), MqttQualityOfService.AtLeastOnce, true));

        var output = Assert.IsType<MqttConnectPacket>(MqttCodec.Decode(MqttCodec.Encode(input)));

        Assert.Equal(input.ClientId, output.ClientId);
        Assert.False(output.CleanSession);
        Assert.Equal((ushort)45, output.KeepAliveSeconds);
        Assert.Equal("device-user", output.Username);
        var outputWill = Assert.IsType<MqttLastWill>(output.LastWill);
        Assert.Equal("plant/a/status", outputWill.Topic);
        Assert.Equal(input.Password, output.Password);
        Assert.Equal(input.LastWill!.Payload, outputWill.Payload);
    }

    [Fact]
    public void EncodeDecode_ConnAck_RoundTrips()
    {
        var output = Assert.IsType<MqttConnAckPacket>(MqttCodec.Decode(MqttCodec.Encode(new MqttConnAckPacket(true, 0))));
        Assert.True(output.SessionPresent);
        Assert.Equal((byte)0, output.ReturnCode);
    }

    [Fact]
    public void EncodeDecode_QosZeroPublish_RoundTrips()
    {
        var output = Assert.IsType<MqttPublishPacket>(MqttCodec.Decode(MqttCodec.Encode(
            new MqttPublishPacket("plants/a/devices/cmp-01/telemetry", [1, 2, 3], MqttQualityOfService.AtMostOnce, true))));
        Assert.Equal("plants/a/devices/cmp-01/telemetry", output.Topic);
        Assert.Equal([1, 2, 3], output.Payload);
        Assert.Equal(MqttQualityOfService.AtMostOnce, output.QualityOfService);
        Assert.True(output.Retain);
        Assert.Null(output.PacketIdentifier);
    }

    [Fact]
    public void EncodeDecode_QosOnePublish_RoundTrips()
    {
        var output = Assert.IsType<MqttPublishPacket>(MqttCodec.Decode(MqttCodec.Encode(
            new MqttPublishPacket("plant/a/state", [5], MqttQualityOfService.AtLeastOnce, false, true, 42))));
        Assert.Equal((ushort)42, output.PacketIdentifier);
        Assert.True(output.Duplicate);
        Assert.Equal(MqttQualityOfService.AtLeastOnce, output.QualityOfService);
    }

    [Fact]
    public void EncodeDecode_PubAck_RoundTrips()
    {
        var output = Assert.IsType<MqttPubAckPacket>(MqttCodec.Decode(MqttCodec.Encode(new MqttPubAckPacket(99))));
        Assert.Equal((ushort)99, output.PacketIdentifier);
    }

    [Fact]
    public void EncodeDecode_Subscribe_RoundTrips()
    {
        var input = new MqttSubscribePacket(7, [
            new MqttSubscription("plants/+/devices/+/telemetry", MqttQualityOfService.AtLeastOnce),
            new MqttSubscription("alerts/#", MqttQualityOfService.AtMostOnce)
        ]);
        var output = Assert.IsType<MqttSubscribePacket>(MqttCodec.Decode(MqttCodec.Encode(input)));
        Assert.Equal((ushort)7, output.PacketIdentifier);
        Assert.Equal(2, output.Subscriptions.Count);
        Assert.Equal("alerts/#", output.Subscriptions[1].TopicFilter);
    }

    [Fact]
    public void EncodeDecode_SubAck_RoundTrips()
    {
        var output = Assert.IsType<MqttSubAckPacket>(MqttCodec.Decode(MqttCodec.Encode(new MqttSubAckPacket(7, [1, 0x80]))));
        Assert.Equal((ushort)7, output.PacketIdentifier);
        Assert.Equal(new byte[] { 1, 0x80 }, output.ReturnCodes);
    }

    [Theory]
    [InlineData(typeof(MqttPingReqPacket))]
    [InlineData(typeof(MqttPingRespPacket))]
    [InlineData(typeof(MqttDisconnectPacket))]
    public void EncodeDecode_EmptyControlPackets_RoundTrip(Type type)
    {
        var input = (MqttPacket)Activator.CreateInstance(type)!;
        var output = MqttCodec.Decode(MqttCodec.Encode(input));
        Assert.Equal(type, output.GetType());
    }

    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(16383)]
    [InlineData(16384)]
    public void EncodeDecode_PublishAcrossRemainingLengthBoundaries_RoundTrips(int remainingLength)
    {
        const int PublishOverhead = 3; // UTF-8 topic "a": two-byte length prefix + one byte.
        var payload = Enumerable.Range(0, remainingLength - PublishOverhead).Select(index => (byte)(index % 251)).ToArray();
        var encoded = MqttCodec.Encode(new MqttPublishPacket("a", payload));

        var decodedLength = MqttCodec.ReadRemainingLength(encoded.AsSpan(1), out var headerLength);
        var output = Assert.IsType<MqttPublishPacket>(MqttCodec.Decode(encoded));

        Assert.Equal(remainingLength, decodedLength);
        Assert.Equal(encoded.Length, 1 + headerLength + remainingLength);
        Assert.Equal(payload, output.Payload);
    }

    [Theory]
    [InlineData("plant/+/telemetry", "plant/a/telemetry", true)]
    [InlineData("plant/+/telemetry", "plant/a/b/telemetry", false)]
    [InlineData("plant/#", "plant/a/b", true)]
    [InlineData("plant/#", "plant", true)]
    [InlineData("plant/+/telemetry", "plant//telemetry", true)]
    [InlineData("#", "$SYS/broker", false)]
    [InlineData("$SYS/#", "$SYS/broker", true)]
    public void TopicFilter_MatchesExpectedTopics(string filter, string topic, bool expected)
    {
        Assert.Equal(expected, TopicFilters.Matches(filter, topic));
    }

    [Theory]
    [InlineData("plant/#/bad")]
    [InlineData("plant/a+")]
    [InlineData("plant/#/")]
    [InlineData("")]
    public void TopicFilter_InvalidFilters_AreRejected(string filter)
    {
        Assert.False(TopicFilters.IsValidFilter(filter));
    }

    [Theory]
    [InlineData(new byte[] { 0x30, 0x80 })]
    [InlineData(new byte[] { 0x30, 0x80, 0x00 })]
    [InlineData(new byte[] { 0x30, 0xFF, 0xFF, 0xFF, 0x80 })]
    [InlineData(new byte[] { 0x82, 0x00 })]
    [InlineData(new byte[] { 0x36, 0x00 })]
    public void Decode_MalformedFrames_ThrowsProtocolException(byte[] frame)
    {
        Assert.Throws<MqttProtocolException>(() => MqttCodec.Decode(frame));
    }

    [Fact]
    public void Encode_QosOneWithoutPacketIdentifier_RejectsPacket()
    {
        Assert.Throws<MqttProtocolException>(() => MqttCodec.Encode(
            new MqttPublishPacket("plant/a", [1], MqttQualityOfService.AtLeastOnce)));
    }

    [Fact]
    public async Task InMemoryTransport_RetainedMessage_ReplaysToNewSubscriber()
    {
        await using var transport = new InMemoryMessageTransport();
        await transport.PublishAsync(new TransportMessage("plant/a/status", Encoding.UTF8.GetBytes("online"), Retain: true));
        var received = new List<TransportMessage>();
        await using var subscription = await transport.SubscribeAsync("plant/+/status", message =>
        {
            received.Add(message);
            return Task.CompletedTask;
        });

        Assert.Single(received);
        Assert.Equal("online", Encoding.UTF8.GetString(received[0].Payload));
    }
}
