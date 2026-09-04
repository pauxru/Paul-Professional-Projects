using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Identity;
using ZeroTrust.Infrastructure.Persistence;

namespace ZeroTrust.Infrastructure.Identity;

public sealed class JwksProvider : IJwksProvider
{
    private readonly ZeroTrustDbContext _db;
    private readonly Application.Abstractions.IClock _clock;

    public JwksProvider(ZeroTrustDbContext db, Application.Abstractions.IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<Jwks> GetAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var keys = await _db.SigningKeys
            .Where(k => k.RetiredAtUtc == null && k.NotBeforeUtc <= now)
            .OrderByDescending(k => k.IsPrimary).ThenByDescending(k => k.NotBeforeUtc)
            .ToListAsync(ct);

        var list = new List<JwksKey>(keys.Count);
        foreach (var k in keys)
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(k.PublicKeyPem);
            var parameters = rsa.ExportParameters(false);
            list.Add(new JwksKey(
                Kid: k.Kid,
                Kty: "RSA",
                Use: "sig",
                Alg: k.Algorithm,
                N: Base64UrlEncoder.Encode(parameters.Modulus!),
                E: Base64UrlEncoder.Encode(parameters.Exponent!)));
        }
        return new Jwks(list);
    }

    public async Task RotateAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var existing = await _db.SigningKeys.Where(k => k.RetiredAtUtc == null).ToListAsync(ct);

        var currentPrimary = existing.FirstOrDefault(x => x.IsPrimary);

        var newKey = CreateRsaKey(isPrimary: true, notBefore: now);
        _db.SigningKeys.Add(newKey);

        if (currentPrimary is not null)
        {
            currentPrimary.Demote();
        }

        var others = existing.Where(k => k != currentPrimary).ToList();
        foreach (var stale in others)
        {
            stale.Retire(now);
        }

        await _db.SaveChangesAsync(ct);
    }

    public static SigningKey CreateRsaKey(bool isPrimary, DateTime notBefore)
    {
        using var rsa = RSA.Create(2048);
        var pubPem = rsa.ExportRSAPublicKeyPem();
        var privPem = rsa.ExportRSAPrivateKeyPem();
        var kid = ComputeKid(rsa);
        return new SigningKey(kid, "RS256", pubPem, privPem, isPrimary, notBefore);
    }

    private static string ComputeKid(RSA rsa)
    {
        var parameters = rsa.ExportParameters(false);
        var thumbInput = Base64UrlEncoder.Encode(parameters.Modulus!) + ":" + Base64UrlEncoder.Encode(parameters.Exponent!);
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(thumbInput));
        return Base64UrlEncoder.Encode(hash)[..16];
    }
}
