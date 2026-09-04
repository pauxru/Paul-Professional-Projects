using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AuditPlatform.Domain.Ids;

/// <summary>
/// UUIDv7 generator. Layout (16 bytes / 128 bits):
///   Bytes 0..5  : 48-bit big-endian Unix timestamp in milliseconds
///   Byte  6     : 4-bit version (0b0111 == 7) | high 4 bits of pseudo-random data
///   Byte  7     : 8 more bits of pseudo-random data
///   Byte  8     : 2-bit variant (0b10) | 6 more bits of pseudo-random data
///   Bytes 9..15 : 56 bits of cryptographically-random data
///
/// Reference: draft-peabody-dispatch-new-uuid-format-04, section 5.2.
/// UUIDv7 sorts lexicographically by time — critical for the append-only hash chain
/// index and for keyset pagination on <see cref="Events.AuditEvent"/>.
/// </summary>
public static class UuidV7
{
    private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

    public static Guid NewGuid(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[16];
        Rng.GetBytes(bytes[6..]);

        var unixMs = timestamp.ToUnixTimeMilliseconds();
        if (unixMs < 0) throw new ArgumentOutOfRangeException(nameof(timestamp), "UUIDv7 requires a positive Unix ms timestamp.");

        Span<byte> ms = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(ms, unixMs);
        // Take the low 48 bits of the timestamp.
        ms[2..].CopyTo(bytes[..6]);

        // Set version (7) in the top 4 bits of byte 6.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        // Set variant (10xx) in the top 2 bits of byte 8.
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return FromRfcBytes(bytes);
    }

    public static Guid NewGuid() => NewGuid(DateTimeOffset.UtcNow);

    public static long ExtractUnixMs(Guid id)
    {
        Span<byte> raw = stackalloc byte[16];
        WriteRfcBytes(id, raw);
        Span<byte> ms = stackalloc byte[8];
        raw[..6].CopyTo(ms[2..]);
        return BinaryPrimitives.ReadInt64BigEndian(ms);
    }

    // .NET's Guid uses a mixed-endian layout for the first three groups. These helpers
    // convert to and from the canonical big-endian (RFC 4122) byte order that UUIDv7
    // demands, so the resulting Guids compare in creation-time order.
    private static Guid FromRfcBytes(ReadOnlySpan<byte> rfc)
    {
        Span<byte> mixed = stackalloc byte[16];
        rfc.CopyTo(mixed);
        // Reverse group 1 (bytes 0..3), group 2 (bytes 4..5), group 3 (bytes 6..7).
        mixed[..4].Reverse();
        mixed[4..6].Reverse();
        mixed[6..8].Reverse();
        return new Guid(mixed);
    }

    private static void WriteRfcBytes(Guid id, Span<byte> rfc)
    {
        Span<byte> mixed = stackalloc byte[16];
        if (!id.TryWriteBytes(mixed)) throw new InvalidOperationException("Failed to write Guid bytes.");
        mixed[..4].Reverse();
        mixed[4..6].Reverse();
        mixed[6..8].Reverse();
        mixed.CopyTo(rfc);
    }
}

public interface IIdGenerator
{
    Guid NewId();
    Guid NewId(DateTimeOffset timestamp);
}

public sealed class UuidV7IdGenerator : IIdGenerator
{
    public Guid NewId() => UuidV7.NewGuid();
    public Guid NewId(DateTimeOffset timestamp) => UuidV7.NewGuid(timestamp);
}
