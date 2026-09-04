using System.Security.Cryptography;
using System.Text;

namespace Auth.Passwords;

/// <summary>
/// Every password hash format this system has to understand, in the order the company
/// acquired them.
/// </summary>
/// <remarks>
/// This list is the actual shape of the problem. A system old enough to be running
/// WS-Federation has almost never been through <i>one</i> hashing scheme -- it has been
/// through two or three, each migration abandoned partway through, so the users table
/// holds a stratigraphy of every security opinion the company has ever had.
/// </remarks>
public enum HashFormat
{
    /// <summary>
    /// SqlMembershipProvider, <c>PasswordFormat="Hashed"</c>. Single unsalted-in-spirit
    /// SHA-1 over salt+password, no iteration count at all. Shipped 2005, still in
    /// production databases in 2026.
    /// </summary>
    MembershipSha1 = 0,

    /// <summary>ASP.NET Identity v2: PBKDF2-HMAC-SHA1, 1000 iterations. Marker byte 0x00.</summary>
    IdentityV2 = 1,

    /// <summary>ASP.NET Identity v3: PBKDF2-HMAC-SHA256/512, configurable iterations. Marker byte 0x01.</summary>
    IdentityV3 = 2,

    /// <summary>The target: Argon2id, RFC 9106.</summary>
    Argon2id = 3,
}

public enum Prf
{
    HmacSha1 = 0,
    HmacSha256 = 1,
    HmacSha512 = 2,
}

/// <summary>
/// A stored credential, parsed from whatever the database happens to hold for this user.
/// </summary>
public sealed record StoredHash(
    HashFormat Format,
    byte[] Salt,
    byte[] Digest,
    int Iterations = 0,
    Prf Prf = Prf.HmacSha1,
    int MemoryKib = 0,
    int Parallelism = 0)
{
    /// <summary>
    /// Work factor expressed as a single comparable number: how many SHA-256-equivalent
    /// compression calls an attacker must perform per guess.
    /// </summary>
    /// <remarks>
    /// Crude on purpose. It ignores the memory term entirely, which is exactly why it
    /// understates Argon2id by orders of magnitude -- the point of Argon2 is that the
    /// attacker's bottleneck stops being compression calls. Reported here so the report can
    /// show that even on the metric most generous to PBKDF2, the legacy formats lose.
    /// </remarks>
    public long AttackerCompressionsPerGuess => Format switch
    {
        HashFormat.MembershipSha1 => 1,
        HashFormat.IdentityV2 => 2L * Iterations,
        HashFormat.IdentityV3 => 2L * Iterations,
        // 2 compressions per 1 KiB block, times blocks, times passes.
        HashFormat.Argon2id => 2L * MemoryKib * Math.Max(1, Iterations),
        _ => throw new InvalidOperationException($"unknown format {Format}"),
    };
}

/// <summary>
/// Parses and formats the stored representations. Deliberately byte-compatible with the
/// real Microsoft formats, because a migration that cannot read the existing column is not
/// a migration.
/// </summary>
public static class HashCodec
{
    public const byte IdentityV2Marker = 0x00;
    public const byte IdentityV3Marker = 0x01;
    public const byte Argon2idMarker = 0x10;

    /// <summary>
    /// SqlMembershipProvider kept salt and hash in two separate columns. Represented here
    /// as <c>"membership:{salt}:{hash}"</c> so one string can carry every format.
    /// </summary>
    public static string Format(StoredHash hash) => hash.Format switch
    {
        HashFormat.MembershipSha1 =>
            $"membership:{Convert.ToBase64String(hash.Salt)}:{Convert.ToBase64String(hash.Digest)}",
        _ => Convert.ToBase64String(Encode(hash)),
    };

    public static byte[] Encode(StoredHash hash)
    {
        switch (hash.Format)
        {
            case HashFormat.IdentityV2:
            {
                var buffer = new byte[1 + hash.Salt.Length + hash.Digest.Length];
                buffer[0] = IdentityV2Marker;
                hash.Salt.CopyTo(buffer, 1);
                hash.Digest.CopyTo(buffer, 1 + hash.Salt.Length);
                return buffer;
            }

            case HashFormat.IdentityV3:
            {
                var buffer = new byte[13 + hash.Salt.Length + hash.Digest.Length];
                buffer[0] = IdentityV3Marker;
                WriteBigEndian(buffer, 1, (uint)hash.Prf);
                WriteBigEndian(buffer, 5, (uint)hash.Iterations);
                WriteBigEndian(buffer, 9, (uint)hash.Salt.Length);
                hash.Salt.CopyTo(buffer, 13);
                hash.Digest.CopyTo(buffer, 13 + hash.Salt.Length);
                return buffer;
            }

            case HashFormat.Argon2id:
            {
                var buffer = new byte[14 + hash.Salt.Length + hash.Digest.Length];
                buffer[0] = Argon2idMarker;
                WriteBigEndian(buffer, 1, (uint)hash.MemoryKib);
                WriteBigEndian(buffer, 5, (uint)hash.Iterations);
                buffer[9] = (byte)hash.Parallelism;
                WriteBigEndian(buffer, 10, (uint)hash.Salt.Length);
                hash.Salt.CopyTo(buffer, 14);
                hash.Digest.CopyTo(buffer, 14 + hash.Salt.Length);
                return buffer;
            }

            default:
                throw new InvalidOperationException(
                    $"{hash.Format} has no single-column encoding");
        }
    }

    public static StoredHash Parse(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        if (stored.StartsWith("membership:", StringComparison.Ordinal))
        {
            var parts = stored.Split(':');
            if (parts.Length != 3) throw new FormatException("malformed membership hash");
            return new StoredHash(
                HashFormat.MembershipSha1,
                Convert.FromBase64String(parts[1]),
                Convert.FromBase64String(parts[2]));
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(stored);
        }
        catch (FormatException)
        {
            throw new FormatException("stored hash is neither a membership pair nor base64");
        }

        return Decode(bytes);
    }

    public static StoredHash Decode(byte[] bytes)
    {
        if (bytes.Length == 0) throw new FormatException("empty hash");

        switch (bytes[0])
        {
            case IdentityV2Marker:
            {
                if (bytes.Length != 1 + 16 + 32) throw new FormatException("bad Identity v2 length");
                return new StoredHash(
                    HashFormat.IdentityV2,
                    bytes[1..17],
                    bytes[17..49],
                    Iterations: 1000,
                    Prf: Prf.HmacSha1);
            }

            case IdentityV3Marker:
            {
                if (bytes.Length < 13) throw new FormatException("bad Identity v3 length");
                var prf = (Prf)ReadBigEndian(bytes, 1);
                var iterations = (int)ReadBigEndian(bytes, 5);
                var saltLength = (int)ReadBigEndian(bytes, 9);
                if (saltLength < 8 || 13 + saltLength >= bytes.Length)
                {
                    throw new FormatException("bad Identity v3 salt length");
                }
                return new StoredHash(
                    HashFormat.IdentityV3,
                    bytes[13..(13 + saltLength)],
                    bytes[(13 + saltLength)..],
                    iterations,
                    prf);
            }

            case Argon2idMarker:
            {
                if (bytes.Length < 14) throw new FormatException("bad Argon2id length");
                var memory = (int)ReadBigEndian(bytes, 1);
                var iterations = (int)ReadBigEndian(bytes, 5);
                var parallelism = bytes[9];
                var saltLength = (int)ReadBigEndian(bytes, 10);
                if (saltLength < 8 || 14 + saltLength >= bytes.Length)
                {
                    throw new FormatException("bad Argon2id salt length");
                }
                return new StoredHash(
                    HashFormat.Argon2id,
                    bytes[14..(14 + saltLength)],
                    bytes[(14 + saltLength)..],
                    iterations,
                    Prf.HmacSha512,
                    memory,
                    parallelism);
            }

            default:
                throw new FormatException($"unknown hash marker 0x{bytes[0]:x2}");
        }
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint ReadBigEndian(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) |
        ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];
}

/// <summary>
/// Computes and verifies every format. The verify path is what runs on the production login
/// endpoint, so its <i>timing</i> is as much a part of its behaviour as its result -- see
/// <c>docs/adr/004-constant-work-verification.md</c>.
/// </summary>
public static class PasswordAlgorithms
{
    public static StoredHash HashMembershipSha1(string password, byte[] salt)
    {
        // UTF-16LE, because that is what .NET's Encoding.Unicode gave the original provider,
        // and changing it now would invalidate every stored hash in the database.
        var passwordBytes = Encoding.Unicode.GetBytes(password);
        var buffer = new byte[salt.Length + passwordBytes.Length];
        salt.CopyTo(buffer, 0);
        passwordBytes.CopyTo(buffer, salt.Length);
        return new StoredHash(HashFormat.MembershipSha1, salt, SHA1.HashData(buffer));
    }

    public static StoredHash HashIdentityV2(string password, byte[] salt) =>
        new(HashFormat.IdentityV2, salt,
            Pbkdf2(password, salt, 1000, Prf.HmacSha1, 32),
            1000, Prf.HmacSha1);

    public static StoredHash HashIdentityV3(
        string password, byte[] salt, int iterations, Prf prf = Prf.HmacSha256) =>
        new(HashFormat.IdentityV3, salt,
            Pbkdf2(password, salt, iterations, prf, 32),
            iterations, prf);

    public static StoredHash HashArgon2id(
        string password, byte[] salt, int memoryKib, int iterations, int parallelism) =>
        new(HashFormat.Argon2id, salt,
            Argon2.Hash(Encoding.UTF8.GetBytes(password), salt,
                        memoryKib, iterations, parallelism, 32),
            iterations, Prf.HmacSha512, memoryKib, parallelism);

    /// <summary>
    /// PBKDF2 comes from the platform. Argon2 does not, so that one is written out and
    /// checked against RFC 9106's vectors. Reimplementing a primitive the runtime already
    /// gets right adds risk and buys nothing.
    /// </summary>
    public static byte[] Pbkdf2(string password, byte[] salt, int iterations, Prf prf, int length) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, ToHashName(prf), length);

    private static HashAlgorithmName ToHashName(Prf prf) => prf switch
    {
        Prf.HmacSha1 => HashAlgorithmName.SHA1,
        Prf.HmacSha256 => HashAlgorithmName.SHA256,
        Prf.HmacSha512 => HashAlgorithmName.SHA512,
        _ => throw new ArgumentOutOfRangeException(nameof(prf)),
    };

    /// <summary>Recomputes <paramref name="stored"/> from a candidate password and compares.</summary>
    public static bool Verify(StoredHash stored, string password)
    {
        var candidate = stored.Format switch
        {
            HashFormat.MembershipSha1 => HashMembershipSha1(password, stored.Salt).Digest,
            HashFormat.IdentityV2 => Pbkdf2(password, stored.Salt, stored.Iterations, stored.Prf, 32),
            HashFormat.IdentityV3 => Pbkdf2(password, stored.Salt, stored.Iterations, stored.Prf,
                                            stored.Digest.Length),
            HashFormat.Argon2id => Argon2.Hash(
                Encoding.UTF8.GetBytes(password), stored.Salt,
                stored.MemoryKib, stored.Iterations, stored.Parallelism, stored.Digest.Length),
            _ => throw new InvalidOperationException($"unknown format {stored.Format}"),
        };

        return CryptographicOperations.FixedTimeEquals(candidate, stored.Digest);
    }
}
