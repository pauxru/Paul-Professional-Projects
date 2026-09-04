using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace Iiot.Protocol;

public static class MqttCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(MqttPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var body = new ArrayBufferWriter<byte>();
        var flags = EncodeBody(packet, body);
        var remainingLength = body.WrittenCount;
        var header = new ArrayBufferWriter<byte>(remainingLength + 5);
        header.GetSpan(1)[0] = (byte)(((byte)packet.Type << 4) | flags);
        header.Advance(1);
        WriteRemainingLength(header, remainingLength);
        body.WrittenSpan.CopyTo(header.GetSpan(remainingLength));
        header.Advance(remainingLength);
        return header.WrittenSpan.ToArray();
    }

    public static MqttPacket Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
        {
            throw new MqttProtocolException("MQTT frame is shorter than a fixed header.");
        }

        var packetType = (MqttPacketType)(frame[0] >> 4);
        var flags = (byte)(frame[0] & 0x0F);
        var remainingLength = ReadRemainingLength(frame[1..], out var lengthBytes);
        var bodyOffset = 1 + lengthBytes;
        if (remainingLength != frame.Length - bodyOffset)
        {
            throw new MqttProtocolException("MQTT remaining length does not match the frame length.");
        }

        var reader = new PacketReader(frame[bodyOffset..]);
        var result = packetType switch
        {
            MqttPacketType.Connect => DecodeConnect(flags, ref reader),
            MqttPacketType.ConnAck => DecodeConnAck(flags, ref reader),
            MqttPacketType.Publish => DecodePublish(flags, ref reader),
            MqttPacketType.PubAck => DecodePubAck(flags, ref reader),
            MqttPacketType.Subscribe => DecodeSubscribe(flags, ref reader),
            MqttPacketType.SubAck => DecodeSubAck(flags, ref reader),
            MqttPacketType.PingReq => DecodeEmpty<MqttPingReqPacket>(flags, ref reader, 0),
            MqttPacketType.PingResp => DecodeEmpty<MqttPingRespPacket>(flags, ref reader, 0),
            MqttPacketType.Disconnect => DecodeEmpty<MqttDisconnectPacket>(flags, ref reader, 0),
            _ => throw new MqttProtocolException($"Unsupported MQTT packet type {(byte)packetType}.")
        };

        if (!reader.End)
        {
            throw new MqttProtocolException("MQTT packet has trailing bytes.");
        }

        return result;
    }

    public static void WriteRemainingLength(IBufferWriter<byte> writer, int value)
    {
        if (value is < 0 or > 268_435_455)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "MQTT remaining length must fit in four bytes.");
        }

        do
        {
            var encoded = value % 128;
            value /= 128;
            if (value > 0)
            {
                encoded |= 0x80;
            }

            writer.GetSpan(1)[0] = (byte)encoded;
            writer.Advance(1);
        }
        while (value > 0);
    }

    public static int ReadRemainingLength(ReadOnlySpan<byte> source, out int bytesRead)
    {
        var value = 0;
        var multiplier = 1;
        bytesRead = 0;

        for (var index = 0; index < 4; index++)
        {
            if (index >= source.Length)
            {
                throw new MqttProtocolException("Truncated MQTT remaining length.");
            }

            var encoded = source[index];
            value += (encoded & 127) * multiplier;
            bytesRead++;

            if ((encoded & 128) == 0)
            {
                if (bytesRead > 1 && value < multiplier)
                {
                    throw new MqttProtocolException("MQTT remaining length is not minimally encoded.");
                }

                return value;
            }

            multiplier *= 128;
        }

        throw new MqttProtocolException("MQTT remaining length exceeds four bytes.");
    }

    public static async Task WritePacketAsync(Stream stream, MqttPacket packet, CancellationToken cancellationToken = default)
    {
        var bytes = Encode(packet);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<MqttPacket> ReadPacketAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var first = new byte[1];
        await ReadExactlyAsync(stream, first, cancellationToken);

        var remainingBytes = new List<byte>(4);
        while (remainingBytes.Count < 4)
        {
            var current = new byte[1];
            await ReadExactlyAsync(stream, current, cancellationToken);
            remainingBytes.Add(current[0]);
            if ((current[0] & 128) == 0)
            {
                break;
            }
        }

        var remaining = ReadRemainingLength(CollectionsMarshal.AsSpan(remainingBytes), out _);
        var body = new byte[remaining];
        await ReadExactlyAsync(stream, body, cancellationToken);
        var frame = new byte[1 + remainingBytes.Count + remaining];
        frame[0] = first[0];
        remainingBytes.CopyTo(frame, 1);
        body.CopyTo(frame, 1 + remainingBytes.Count);
        return Decode(frame);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> target, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < target.Length)
        {
            var amount = await stream.ReadAsync(target[read..], cancellationToken);
            if (amount == 0)
            {
                throw new EndOfStreamException("MQTT peer closed the stream in the middle of a packet.");
            }

            read += amount;
        }
    }

    private static byte EncodeBody(MqttPacket packet, ArrayBufferWriter<byte> writer)
    {
        switch (packet)
        {
            case MqttConnectPacket connect:
                WriteString(writer, "MQTT");
                WriteByte(writer, 4);
                var connectFlags = (byte)(connect.CleanSession ? 0x02 : 0);
                if (connect.LastWill is not null)
                {
                    ValidateWill(connect.LastWill);
                    connectFlags |= 0x04;
                    connectFlags |= (byte)((byte)connect.LastWill.QualityOfService << 3);
                    if (connect.LastWill.Retain)
                    {
                        connectFlags |= 0x20;
                    }
                }

                if (connect.Password is not null)
                {
                    connectFlags |= 0x40;
                }

                if (connect.Username is not null)
                {
                    connectFlags |= 0x80;
                }

                WriteByte(writer, connectFlags);
                WriteUInt16(writer, connect.KeepAliveSeconds);
                WriteString(writer, connect.ClientId);
                if (connect.LastWill is not null)
                {
                    WriteString(writer, connect.LastWill.Topic);
                    WriteBinary(writer, connect.LastWill.Payload);
                }

                if (connect.Username is not null)
                {
                    WriteString(writer, connect.Username);
                }

                if (connect.Password is not null)
                {
                    WriteBinary(writer, connect.Password);
                }

                return 0;

            case MqttConnAckPacket connAck:
                if (connAck.ReturnCode != 0 && connAck.SessionPresent)
                {
                    throw new MqttProtocolException("CONNACK must not set session present for a rejected connection.");
                }

                WriteByte(writer, connAck.SessionPresent ? (byte)1 : (byte)0);
                WriteByte(writer, connAck.ReturnCode);
                return 0;

            case MqttPublishPacket publish:
                ValidatePublish(publish);
                WriteString(writer, publish.Topic);
                if (publish.QualityOfService == MqttQualityOfService.AtLeastOnce)
                {
                    WriteUInt16(writer, publish.PacketIdentifier!.Value);
                }

                WriteBytes(writer, publish.Payload);
                return (byte)((publish.Duplicate ? 0x08 : 0) | ((byte)publish.QualityOfService << 1) | (publish.Retain ? 1 : 0));

            case MqttPubAckPacket pubAck:
                WritePacketIdentifier(writer, pubAck.PacketIdentifier);
                return 0;

            case MqttSubscribePacket subscribe:
                if (subscribe.Subscriptions.Count == 0)
                {
                    throw new MqttProtocolException("SUBSCRIBE must contain at least one filter.");
                }

                WritePacketIdentifier(writer, subscribe.PacketIdentifier);
                foreach (var subscription in subscribe.Subscriptions)
                {
                    if (!TopicFilters.IsValidFilter(subscription.TopicFilter) ||
                        subscription.QualityOfService > MqttQualityOfService.AtLeastOnce)
                    {
                        throw new MqttProtocolException("Invalid MQTT subscription.");
                    }

                    WriteString(writer, subscription.TopicFilter);
                    WriteByte(writer, (byte)subscription.QualityOfService);
                }

                return 0x02;

            case MqttSubAckPacket subAck:
                if (subAck.ReturnCodes.Count == 0 || subAck.ReturnCodes.Any(code => code is not (0 or 1 or 0x80)))
                {
                    throw new MqttProtocolException("Invalid SUBACK return code.");
                }

                WritePacketIdentifier(writer, subAck.PacketIdentifier);
                foreach (var code in subAck.ReturnCodes)
                {
                    WriteByte(writer, code);
                }

                return 0;

            case MqttPingReqPacket:
            case MqttPingRespPacket:
            case MqttDisconnectPacket:
                return 0;

            default:
                throw new MqttProtocolException($"Unsupported packet {packet.Type}.");
        }
    }

    private static MqttPacket DecodeConnect(byte flags, ref PacketReader reader)
    {
        EnsureFlags(flags, 0);
        if (reader.ReadString() != "MQTT" || reader.ReadByte() != 4)
        {
            throw new MqttProtocolException("Only MQTT protocol level 4 (3.1.1) is supported.");
        }

        var connectFlags = reader.ReadByte();
        if ((connectFlags & 1) != 0)
        {
            throw new MqttProtocolException("CONNECT reserved flag must be zero.");
        }

        var willFlag = (connectFlags & 0x04) != 0;
        var willQos = (byte)((connectFlags >> 3) & 0x03);
        var willRetain = (connectFlags & 0x20) != 0;
        if ((!willFlag && (willQos != 0 || willRetain)) || willQos > 1)
        {
            throw new MqttProtocolException("Invalid CONNECT last-will flags.");
        }

        var passwordFlag = (connectFlags & 0x40) != 0;
        var usernameFlag = (connectFlags & 0x80) != 0;
        if (passwordFlag && !usernameFlag)
        {
            throw new MqttProtocolException("MQTT CONNECT password requires a username.");
        }

        var keepAlive = reader.ReadUInt16();
        var clientId = reader.ReadString();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new MqttProtocolException("MQTT client id cannot be empty.");
        }

        MqttLastWill? will = null;
        if (willFlag)
        {
            will = new MqttLastWill(reader.ReadString(), reader.ReadBinary(), (MqttQualityOfService)willQos, willRetain);
            ValidateWill(will);
        }

        var username = usernameFlag ? reader.ReadString() : null;
        var password = passwordFlag ? reader.ReadBinary() : null;
        return new MqttConnectPacket(clientId, (connectFlags & 0x02) != 0, keepAlive, username, password, will);
    }

    private static MqttPacket DecodeConnAck(byte flags, ref PacketReader reader)
    {
        EnsureFlags(flags, 0);
        var ackFlags = reader.ReadByte();
        if ((ackFlags & 0xFE) != 0)
        {
            throw new MqttProtocolException("Invalid CONNACK flags.");
        }

        var returnCode = reader.ReadByte();
        if (returnCode > 5 || (returnCode != 0 && ackFlags != 0))
        {
            throw new MqttProtocolException("Invalid CONNACK return code.");
        }

        return new MqttConnAckPacket(ackFlags == 1, returnCode);
    }

    private static MqttPacket DecodePublish(byte flags, ref PacketReader reader)
    {
        var qos = (byte)((flags >> 1) & 0x03);
        if (qos > 1)
        {
            throw new MqttProtocolException("QoS 2 is not supported by this MQTT 3.1.1 implementation.");
        }

        var topic = reader.ReadString();
        if (!TopicFilters.IsValidTopicName(topic))
        {
            throw new MqttProtocolException("PUBLISH requires a valid topic name.");
        }

        ushort? identifier = qos == 1 ? reader.ReadPacketIdentifier() : null;
        var payload = reader.ReadRemaining();
        return new MqttPublishPacket(
            topic,
            payload,
            (MqttQualityOfService)qos,
            (flags & 1) != 0,
            (flags & 8) != 0,
            identifier);
    }

    private static MqttPacket DecodePubAck(byte flags, ref PacketReader reader)
    {
        EnsureFlags(flags, 0);
        return new MqttPubAckPacket(reader.ReadPacketIdentifier());
    }

    private static MqttPacket DecodeSubscribe(byte flags, ref PacketReader reader)
    {
        EnsureFlags(flags, 0x02);
        var identifier = reader.ReadPacketIdentifier();
        var subscriptions = new List<MqttSubscription>();
        while (!reader.End)
        {
            var filter = reader.ReadString();
            var qos = reader.ReadByte();
            if (!TopicFilters.IsValidFilter(filter) || qos > 1)
            {
                throw new MqttProtocolException("Invalid SUBSCRIBE filter or QoS.");
            }

            subscriptions.Add(new MqttSubscription(filter, (MqttQualityOfService)qos));
        }

        if (subscriptions.Count == 0)
        {
            throw new MqttProtocolException("SUBSCRIBE has no topic filters.");
        }

        return new MqttSubscribePacket(identifier, subscriptions);
    }

    private static MqttPacket DecodeSubAck(byte flags, ref PacketReader reader)
    {
        EnsureFlags(flags, 0);
        var identifier = reader.ReadPacketIdentifier();
        var returnCodes = new List<byte>();
        while (!reader.End)
        {
            var code = reader.ReadByte();
            if (code is not (0 or 1 or 0x80))
            {
                throw new MqttProtocolException("Invalid SUBACK return code.");
            }

            returnCodes.Add(code);
        }

        if (returnCodes.Count == 0)
        {
            throw new MqttProtocolException("SUBACK has no return codes.");
        }

        return new MqttSubAckPacket(identifier, returnCodes);
    }

    private static T DecodeEmpty<T>(byte flags, ref PacketReader reader, byte expectedFlags)
        where T : MqttPacket, new()
    {
        EnsureFlags(flags, expectedFlags);
        if (!reader.End)
        {
            throw new MqttProtocolException("MQTT control packet should have an empty body.");
        }

        return new T();
    }

    private static void ValidateWill(MqttLastWill will)
    {
        if (!TopicFilters.IsValidTopicName(will.Topic) || will.QualityOfService > MqttQualityOfService.AtLeastOnce)
        {
            throw new MqttProtocolException("Invalid MQTT last will.");
        }
    }

    private static void ValidatePublish(MqttPublishPacket publish)
    {
        if (!TopicFilters.IsValidTopicName(publish.Topic) || publish.QualityOfService > MqttQualityOfService.AtLeastOnce)
        {
            throw new MqttProtocolException("Invalid MQTT PUBLISH.");
        }

        if (publish.QualityOfService == MqttQualityOfService.AtLeastOnce)
        {
            if (publish.PacketIdentifier is not { } identifier || identifier == 0)
            {
                throw new MqttProtocolException("QoS 1 PUBLISH requires a non-zero packet identifier.");
            }
        }
        else if (publish.PacketIdentifier is not null)
        {
            throw new MqttProtocolException("QoS 0 PUBLISH must not include a packet identifier.");
        }
    }

    private static void EnsureFlags(byte actual, byte expected)
    {
        if (actual != expected)
        {
            throw new MqttProtocolException("Invalid MQTT fixed-header flags.");
        }
    }

    private static void WritePacketIdentifier(ArrayBufferWriter<byte> writer, ushort identifier)
    {
        if (identifier == 0)
        {
            throw new MqttProtocolException("MQTT packet identifier cannot be zero.");
        }

        WriteUInt16(writer, identifier);
    }

    private static void WriteString(ArrayBufferWriter<byte> writer, string value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\0'))
        {
            throw new MqttProtocolException("MQTT UTF-8 fields cannot be empty or contain null.");
        }

        var bytes = StrictUtf8.GetBytes(value);
        WriteBinary(writer, bytes);
    }

    private static void WriteBinary(ArrayBufferWriter<byte> writer, byte[] value)
    {
        if (value.Length > ushort.MaxValue)
        {
            throw new MqttProtocolException("MQTT binary field exceeds UInt16 length.");
        }

        WriteUInt16(writer, (ushort)value.Length);
        WriteBytes(writer, value);
    }

    private static void WriteUInt16(ArrayBufferWriter<byte> writer, ushort value)
    {
        var span = writer.GetSpan(2);
        span[0] = (byte)(value >> 8);
        span[1] = (byte)value;
        writer.Advance(2);
    }

    private static void WriteByte(ArrayBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    private static void WriteBytes(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        value.CopyTo(writer.GetSpan(value.Length));
        writer.Advance(value.Length);
    }

    private ref struct PacketReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public PacketReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public bool End => _position == _data.Length;

        public byte ReadByte()
        {
            if (_position >= _data.Length)
            {
                throw new MqttProtocolException("Unexpected end of MQTT packet.");
            }

            return _data[_position++];
        }

        public ushort ReadUInt16()
        {
            if (_data.Length - _position < 2)
            {
                throw new MqttProtocolException("Unexpected end of MQTT UInt16 field.");
            }

            var value = (ushort)((_data[_position] << 8) | _data[_position + 1]);
            _position += 2;
            return value;
        }

        public ushort ReadPacketIdentifier()
        {
            var identifier = ReadUInt16();
            if (identifier == 0)
            {
                throw new MqttProtocolException("MQTT packet identifier cannot be zero.");
            }

            return identifier;
        }

        public string ReadString()
        {
            var length = ReadUInt16();
            if (length == 0 || _data.Length - _position < length)
            {
                throw new MqttProtocolException("Invalid MQTT UTF-8 field length.");
            }

            try
            {
                var result = StrictUtf8.GetString(_data.Slice(_position, length));
                _position += length;
                if (result.Contains('\0'))
                {
                    throw new MqttProtocolException("MQTT UTF-8 field contains null.");
                }

                return result;
            }
            catch (DecoderFallbackException exception)
            {
                throw new MqttProtocolException($"Invalid UTF-8 in MQTT field: {exception.Message}");
            }
        }

        public byte[] ReadBinary()
        {
            var length = ReadUInt16();
            if (_data.Length - _position < length)
            {
                throw new MqttProtocolException("Invalid MQTT binary field length.");
            }

            var value = _data.Slice(_position, length).ToArray();
            _position += length;
            return value;
        }

        public byte[] ReadRemaining()
        {
            var value = _data[_position..].ToArray();
            _position = _data.Length;
            return value;
        }
    }
}
