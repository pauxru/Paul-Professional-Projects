using System.Security.Cryptography;
using System.Text;

namespace Auth.Legacy;

/// <summary>
/// The .NET Framework Forms authentication ticket, field for field.
/// </summary>
/// <remarks>
/// Worth noticing what is <i>in</i> here: the ticket carries the user's identity and an
/// opaque <see cref="UserData"/> string that applications overwhelmingly used to carry
/// roles. That is the whole authorization decision, sitting in a cookie, protected by a key
/// from web.config. No lookup against the database ever happens to contradict it, which is
/// why the integrity of this one blob is load-bearing for the entire application.
/// </remarks>
public sealed record FormsTicket(
    int Version,
    string Name,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    bool IsPersistent,
    string UserData,
    string CookiePath)
{
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresUtc;

    public IReadOnlyList<string> Roles =>
        UserData.Length == 0
            ? []
            : UserData.Split('|', StringSplitOptions.RemoveEmptyEntries);

    public byte[] Serialize()
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8);
        writer.Write((byte)Version);
        writer.Write(Name);
        writer.Write(IssuedUtc.UtcTicks);
        writer.Write(ExpiresUtc.UtcTicks);
        writer.Write(IsPersistent);
        writer.Write(UserData);
        writer.Write(CookiePath);
        writer.Flush();
        return buffer.ToArray();
    }

    public static FormsTicket Deserialize(byte[] bytes)
    {
        using var buffer = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(buffer, Encoding.UTF8);
        var version = reader.ReadByte();
        var name = reader.ReadString();
        var issued = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var expires = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var persistent = reader.ReadBoolean();
        var userData = reader.ReadString();
        var path = reader.ReadString();
        return new FormsTicket(version, name, issued, expires, persistent, userData, path);
    }
}

/// <summary>
/// Why a ticket was rejected.
/// </summary>
/// <remarks>
/// This enum exists so the report can assert a property, not so callers can branch on it.
/// A protector that lets a remote caller distinguish these values -- through a status code,
/// an error page, or a response time -- hands the caller a decision procedure about a key
/// it does not hold. Every value other than <see cref="None"/> must be indistinguishable
/// from outside the process.
/// </remarks>
public enum TicketFailure
{
    None,
    BadPadding,
    BadMac,
    Malformed,
    Expired,
}

public sealed record UnprotectResult(FormsTicket? Ticket, TicketFailure Failure)
{
    public bool Ok => Failure == TicketFailure.None;
}

public interface ITicketProtector
{
    string Protect(FormsTicket ticket);

    UnprotectResult Unprotect(string protectedTicket, DateTimeOffset now);
}

/// <summary>
/// Ticket protection in the composition order ASP.NET originally shipped: authenticate the
/// plaintext, append the tag, then encrypt the whole thing.
/// </summary>
/// <remarks>
/// <para>
/// MAC-then-encrypt is not a typo; it is what the platform did, and it is the reason a
/// generation of ASP.NET applications were remotely compromisable in 2010. The structural
/// problem is ordering: the tag lives <i>inside</i> the ciphertext, so the receiver has no
/// way to check integrity without first decrypting. Decryption therefore runs on attacker
/// controlled bytes, and the CBC unpadding step that follows can fail in a way that is
/// distinguishable from a tag mismatch. Any such distinction turns the server into a
/// decryption service for its own cookies.
/// </para>
/// <para>
/// This class is kept in the codebase for one reason: the migration has to interoperate with
/// tickets already sitting in users' browsers, and a coexistence design that cannot read the
/// old format cannot exist. It is reachable only through
/// <see cref="LegacyTicketProtector"/> and the report measures what it costs to keep it
/// enabled. See <c>docs/adr/003-legacy-ticket-acceptance-window.md</c>.
/// </para>
/// </remarks>
public sealed class LegacyTicketProtector : ITicketProtector
{
    private readonly byte[] _encryptionKey;
    private readonly byte[] _validationKey;

    public LegacyTicketProtector(byte[] encryptionKey, byte[] validationKey)
    {
        _encryptionKey = encryptionKey;
        _validationKey = validationKey;
    }

    public string Protect(FormsTicket ticket)
    {
        var plaintext = ticket.Serialize();
        var tag = HMACSHA1.HashData(_validationKey, plaintext);

        var payload = new byte[plaintext.Length + tag.Length];
        plaintext.CopyTo(payload, 0);
        tag.CopyTo(payload, plaintext.Length);

        using var aes = Aes.Create();
        aes.Key = _encryptionKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        var ciphertext = aes.EncryptCbc(payload, aes.IV, PaddingMode.PKCS7);
        var output = new byte[aes.IV.Length + ciphertext.Length];
        aes.IV.CopyTo(output, 0);
        ciphertext.CopyTo(output, aes.IV.Length);
        return Convert.ToHexString(output);
    }

    /// <summary>
    /// Decrypts, then checks the tag. The failure reason is returned to the caller so tests
    /// can assert on it; the composition above means the reasons are genuinely different
    /// events, which is precisely the defect. <see cref="HardenedTicketProtector"/> makes
    /// them the same event.
    /// </summary>
    public UnprotectResult Unprotect(string protectedTicket, DateTimeOffset now)
    {
        byte[] input;
        try
        {
            input = Convert.FromHexString(protectedTicket);
        }
        catch (FormatException)
        {
            return new UnprotectResult(null, TicketFailure.Malformed);
        }

        if (input.Length < 32 || (input.Length - 16) % 16 != 0)
        {
            return new UnprotectResult(null, TicketFailure.Malformed);
        }

        var iv = input[..16];
        var ciphertext = input[16..];

        byte[] payload;
        try
        {
            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            payload = aes.DecryptCbc(ciphertext, iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            return new UnprotectResult(null, TicketFailure.BadPadding);
        }

        if (payload.Length < 20) return new UnprotectResult(null, TicketFailure.Malformed);

        var plaintext = payload[..^20];
        var tag = payload[^20..];
        var expected = HMACSHA1.HashData(_validationKey, plaintext);
        if (!CryptographicOperations.FixedTimeEquals(tag, expected))
        {
            return new UnprotectResult(null, TicketFailure.BadMac);
        }

        FormsTicket ticket;
        try
        {
            ticket = FormsTicket.Deserialize(plaintext);
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentException)
        {
            return new UnprotectResult(null, TicketFailure.Malformed);
        }

        return ticket.IsExpired(now)
            ? new UnprotectResult(null, TicketFailure.Expired)
            : new UnprotectResult(ticket, TicketFailure.None);
    }
}

/// <summary>
/// The same ticket, composed the other way round: encrypt, then authenticate everything the
/// receiver will act on, including the IV.
/// </summary>
/// <remarks>
/// <para>
/// Encrypt-then-MAC removes the oracle by construction rather than by care. The tag is
/// checked over the raw bytes before any decryption happens, so a tampered ticket is
/// rejected without the cipher ever running, and there is nothing left for a failure mode to
/// distinguish. This is the difference between "we handled the error paths consistently" --
/// a property somebody has to keep re-establishing on every future edit -- and "the
/// dangerous code is unreachable".
/// </para>
/// <para>
/// It is deliberately wire-incompatible with <see cref="LegacyTicketProtector"/>: a
/// version byte leads the payload, so the two can be told apart without trial decryption
/// and the legacy reader can be switched off on a date rather than discovered still running
/// three years later.
/// </para>
/// </remarks>
public sealed class HardenedTicketProtector : ITicketProtector
{
    public const byte FormatVersion = 0x02;

    private readonly byte[] _encryptionKey;
    private readonly byte[] _validationKey;

    public HardenedTicketProtector(byte[] encryptionKey, byte[] validationKey)
    {
        _encryptionKey = encryptionKey;
        _validationKey = validationKey;
    }

    public string Protect(FormsTicket ticket)
    {
        using var aes = Aes.Create();
        aes.Key = _encryptionKey;
        aes.GenerateIV();

        var ciphertext = aes.EncryptCbc(ticket.Serialize(), aes.IV, PaddingMode.PKCS7);

        var authenticated = new byte[1 + aes.IV.Length + ciphertext.Length];
        authenticated[0] = FormatVersion;
        aes.IV.CopyTo(authenticated, 1);
        ciphertext.CopyTo(authenticated, 1 + aes.IV.Length);

        var tag = HMACSHA256.HashData(_validationKey, authenticated);

        var output = new byte[authenticated.Length + tag.Length];
        authenticated.CopyTo(output, 0);
        tag.CopyTo(output, authenticated.Length);
        return Convert.ToHexString(output);
    }

    public UnprotectResult Unprotect(string protectedTicket, DateTimeOffset now)
    {
        byte[] input;
        try
        {
            input = Convert.FromHexString(protectedTicket);
        }
        catch (FormatException)
        {
            return new UnprotectResult(null, TicketFailure.BadMac);
        }

        // Every rejection before the key is used reports BadMac, not a more specific
        // reason. The specific reason is real and knowable here, and telling anyone is what
        // built the oracle in the first place.
        if (input.Length < 1 + 16 + 16 + 32 || input[0] != FormatVersion)
        {
            return new UnprotectResult(null, TicketFailure.BadMac);
        }

        var authenticated = input[..^32];
        var tag = input[^32..];
        var expected = HMACSHA256.HashData(_validationKey, authenticated);
        if (!CryptographicOperations.FixedTimeEquals(tag, expected))
        {
            return new UnprotectResult(null, TicketFailure.BadMac);
        }

        // Only now is the cipher allowed to see these bytes.
        var iv = authenticated[1..17];
        var ciphertext = authenticated[17..];

        byte[] plaintext;
        try
        {
            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            plaintext = aes.DecryptCbc(ciphertext, iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            return new UnprotectResult(null, TicketFailure.BadMac);
        }

        FormsTicket ticket;
        try
        {
            ticket = FormsTicket.Deserialize(plaintext);
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentException)
        {
            return new UnprotectResult(null, TicketFailure.BadMac);
        }

        // Expiry is deliberately still distinguishable. It is not a key-dependent fact: the
        // holder of a genuine expired ticket already knows when it expired, so saying so
        // leaks nothing, and "your session ended" is a materially better experience than
        // "authentication failed".
        return ticket.IsExpired(now)
            ? new UnprotectResult(null, TicketFailure.Expired)
            : new UnprotectResult(ticket, TicketFailure.None);
    }
}
