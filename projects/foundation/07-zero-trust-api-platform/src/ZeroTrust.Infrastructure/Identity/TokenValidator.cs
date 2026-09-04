using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Infrastructure.Persistence;
using ZeroTrust.Infrastructure.Security;

namespace ZeroTrust.Infrastructure.Identity;

public sealed class TokenValidator : ITokenValidator
{
    private readonly ZeroTrustDbContext _db;
    private readonly TokenIssuerOptions _options;
    private readonly Application.Abstractions.IClock _clock;

    public TokenValidator(ZeroTrustDbContext db, IOptions<TokenIssuerOptions> options, Application.Abstractions.IClock clock)
    {
        _db = db;
        _options = options.Value;
        _clock = clock;
    }

    public async Task<TokenValidationOutcome> ValidateAsync(string bearerToken, string expectedAudience, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            return Fail("no_token");
        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(bearerToken)) return Fail("malformed_token");
            var jwt = handler.ReadJwtToken(bearerToken);

            // Reject alg=none tampering explicitly.
            if (string.Equals(jwt.Header.Alg, "none", StringComparison.OrdinalIgnoreCase))
                return Fail("alg_none_rejected");

            var keys = new List<SecurityKey>();
            if (jwt.Header.Alg == SecurityAlgorithms.RsaSha256)
            {
                var activeKeys = await _db.SigningKeys
                    .Where(k => k.RetiredAtUtc == null)
                    .ToListAsync(ct);
                foreach (var k in activeKeys)
                {
                    var rsa = RSA.Create();
                    rsa.ImportFromPem(k.PublicKeyPem);
                    keys.Add(new RsaSecurityKey(rsa) { KeyId = k.Kid });
                }
            }
            else if (jwt.Header.Alg == SecurityAlgorithms.HmacSha256)
            {
                keys.Add(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.HsSigningKey)) { KeyId = "hs-default" });
            }
            else
            {
                return Fail("unsupported_alg");
            }

            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _options.Issuer,
                ValidateAudience = true,
                ValidAudience = expectedAudience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = keys,
                ValidateLifetime = true,
                LifetimeValidator = (nb, exp, _, _) =>
                    (!nb.HasValue || nb.Value <= _clock.UtcNow) &&
                    (exp.HasValue && exp.Value > _clock.UtcNow),
                ClockSkew = TimeSpan.Zero,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
            };

            var principal = handler.ValidateToken(bearerToken, parameters, out var validated);
            var validatedJwt = (JwtSecurityToken)validated;

            var jti = validatedJwt.Id;
            if (!string.IsNullOrEmpty(jti))
            {
                var revoked = await _db.RevokedTokens.AnyAsync(r => r.Jti == jti, ct);
                if (revoked) return Fail("token_revoked");
            }

            var scopes = (principal.FindFirst("scope")?.Value ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
            var roles = principal.FindAll("roles").Select(c => c.Value).ToArray();
            var sub = principal.FindFirst("sub")?.Value;
            var amr = principal.FindFirst("amr")?.Value;
            var acr = principal.FindFirst("acr")?.Value;

            return new TokenValidationOutcome(
                IsValid: true,
                Subject: sub,
                Audience: expectedAudience,
                Issuer: _options.Issuer,
                Jti: jti,
                Scopes: scopes,
                Roles: roles,
                Amr: amr,
                Acr: acr,
                ExpiresAtUtc: validatedJwt.ValidTo,
                FailureReason: null);
        }
        catch (SecurityTokenExpiredException) { return Fail("expired"); }
        catch (SecurityTokenInvalidAudienceException) { return Fail("wrong_audience"); }
        catch (SecurityTokenInvalidIssuerException) { return Fail("wrong_issuer"); }
        catch (SecurityTokenSignatureKeyNotFoundException) { return Fail("kid_not_found"); }
        catch (SecurityTokenInvalidSignatureException) { return Fail("invalid_signature"); }
        catch (SecurityTokenException ex) { return Fail($"invalid:{ex.Message}"); }
        catch (Exception) { return Fail("validation_error"); }
    }

    private static TokenValidationOutcome Fail(string reason) => new(
        IsValid: false, Subject: null, Audience: null, Issuer: null, Jti: null,
        Scopes: Array.Empty<string>(), Roles: Array.Empty<string>(),
        Amr: null, Acr: null, ExpiresAtUtc: null, FailureReason: reason);
}
