namespace Iiot.Protocol;

public enum MqttPacketType : byte
{
    Connect = 1,
    ConnAck = 2,
    Publish = 3,
    PubAck = 4,
    Subscribe = 8,
    SubAck = 9,
    PingReq = 12,
    PingResp = 13,
    Disconnect = 14
}

public enum MqttQualityOfService : byte
{
    AtMostOnce = 0,
    AtLeastOnce = 1
}

public abstract record MqttPacket(MqttPacketType Type);

public sealed record MqttLastWill(
    string Topic,
    byte[] Payload,
    MqttQualityOfService QualityOfService = MqttQualityOfService.AtMostOnce,
    bool Retain = false);

public sealed record MqttConnectPacket(
    string ClientId,
    bool CleanSession = true,
    ushort KeepAliveSeconds = 30,
    string? Username = null,
    byte[]? Password = null,
    MqttLastWill? LastWill = null) : MqttPacket(MqttPacketType.Connect);

public sealed record MqttConnAckPacket(bool SessionPresent, byte ReturnCode) : MqttPacket(MqttPacketType.ConnAck);

public sealed record MqttPublishPacket(
    string Topic,
    byte[] Payload,
    MqttQualityOfService QualityOfService = MqttQualityOfService.AtMostOnce,
    bool Retain = false,
    bool Duplicate = false,
    ushort? PacketIdentifier = null) : MqttPacket(MqttPacketType.Publish);

public sealed record MqttPubAckPacket(ushort PacketIdentifier) : MqttPacket(MqttPacketType.PubAck);

public sealed record MqttSubscription(string TopicFilter, MqttQualityOfService QualityOfService);

public sealed record MqttSubscribePacket(
    ushort PacketIdentifier,
    IReadOnlyList<MqttSubscription> Subscriptions) : MqttPacket(MqttPacketType.Subscribe);

public sealed record MqttSubAckPacket(
    ushort PacketIdentifier,
    IReadOnlyList<byte> ReturnCodes) : MqttPacket(MqttPacketType.SubAck);

public sealed record MqttPingReqPacket() : MqttPacket(MqttPacketType.PingReq);

public sealed record MqttPingRespPacket() : MqttPacket(MqttPacketType.PingResp);

public sealed record MqttDisconnectPacket() : MqttPacket(MqttPacketType.Disconnect);

public sealed class MqttProtocolException(string message) : IOException(message);
