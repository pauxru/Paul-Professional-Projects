namespace NotificationPlatform.Infrastructure.Security;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NotificationPlatform.Application.Abstractions;
using NotificationPlatform.Application.Options;

/// <summary>
/// Compact HS256-based unsubscribe token: base64url(payload).base64url(sig).
/// Payload layout (binary, 44 bytes):
///   [16 bytes tenantId][16 bytes recipientId][8 bytes issuedAtUnix][4 bytes category-hash]
/// Signed with HMAC-SHA256 over the payload bytes; constant-time compare on verify.
/// </summary>
public sealed class UnsubscribeTokenService : IUnsubscribeTokenService
{
    private readonly WebhookOptions _options;
    private readonly NotificationOptions _notifOptions;
    private readonly Dictionary<uint, string> _categoryLookup = new();

    public UnsubscribeTokenService(IOptions<WebhookOptions> options, IOptions<NotificationOptions> notifOptions)
    {
        _options = options.Value;
        _notifOptions = notifOptions.Value;
    }

    public string Issue(Guid tenantId, Guid recipientId, string category, DateTimeOffset issuedAt, TimeSpan lifetime)
    {
        var buf = new byte[44];
        tenantId.TryWriteBytes(buf.AsSpan(0, 16));
        recipientId.TryWriteBytes(buf.AsSpan(16, 16));
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(32, 8), issuedAt.ToUnixTimeSeconds());
        var catHash = CategoryHash(category);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(40, 4), catHash);
        _categoryLookup[catHash] = category;

        var sig = ComputeSig(buf);
        return Base64Url(buf) + "." + Base64Url(sig);
    }

    public UnsubscribeTokenResult Validate(string token, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new UnsubscribeTokenResult(false, "missing_token", Guid.Empty, Guid.Empty, string.Empty);

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot >= token.Length - 1)
            return new UnsubscribeTokenResult(false, "malformed_token", Guid.Empty, Guid.Empty, string.Empty);

        byte[] payload, sig;
        try
        {
            payload = Base64UrlDecode(token[..dot]);
            sig = Base64UrlDecode(token[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return new UnsubscribeTokenResult(false, "malformed_token", Guid.Empty, Guid.Empty, string.Empty);
        }

        if (payload.Length != 44)
            return new UnsubscribeTokenResult(false, "malformed_payload", Guid.Empty, Guid.Empty, string.Empty);

        var expected = ComputeSig(payload);
        if (!CryptographicOperations.FixedTimeEquals(expected, sig))
            return new UnsubscribeTokenResult(false, "invalid_signature", Guid.Empty, Guid.Empty, string.Empty);

        var tenantId = new Guid(payload.AsSpan(0, 16));
        var recipientId = new Guid(payload.AsSpan(16, 16));
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(32, 8)));
        var catHash = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(40, 4));
        var category = _categoryLookup.GetValueOrDefault(catHash) ?? catHash.ToString("x8");

        var lifetime = TimeSpan.FromDays(_notifOptions.UnsubscribeTokenLifetimeDays);
        if (now - issuedAt > lifetime)
            return new UnsubscribeTokenResult(false, "expired", tenantId, recipientId, category);

        return new UnsubscribeTokenResult(true, null, tenantId, recipientId, category);
    }

    private byte[] ComputeSig(byte[] payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(_options.SigningKey + "|unsubscribe");
        using var hmac = new HMACSHA256(keyBytes);
        return hmac.ComputeHash(payload);
    }

    private static uint CategoryHash(string category)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(category.ToLowerInvariant()));
        return BinaryPrimitives.ReadUInt32BigEndian(hash);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
