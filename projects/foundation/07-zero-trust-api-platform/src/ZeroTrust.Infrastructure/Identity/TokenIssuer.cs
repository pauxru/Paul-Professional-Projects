using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Audit;
using ZeroTrust.Domain.Identity;
using ZeroTrust.Infrastructure.Persistence;
using ZeroTrust.Infrastructure.Security;

namespace ZeroTrust.Infrastructure.Identity;

public sealed class TokenIssuer : ITokenIssuer
{
    private readonly ZeroTrustDbContext _db;
    private readonly Application.Abstractions.IClock _clock;
    private readonly TokenIssuerOptions _options;
    private readonly IAuditLog _audit;

    public TokenIssuer(ZeroTrustDbContext db, Application.Abstractions.IClock clock,
        IOptions<TokenIssuerOptions> options, IAuditLog audit)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
        _audit = audit;
    }

    public async Task<TokenResponse> IssueAsync(TokenRequest request, CancellationToken ct)
    {
        return request.GrantType switch
        {
            "password" => await IssueUserAsync(request, ct),
            "client_credentials" => await IssueClientCredentialsAsync(request, ct),
            "refresh_token" => await RefreshAsync(request, ct),
            "service_account" => await IssueServiceAsync(request, ct),
            _ => throw new InvalidOperationException("unsupported grant_type")
        };
    }

    public async Task RevokeAsync(string jti, string subject, DateTime expiresAtUtc, string reason, CancellationToken ct)
    {
        _db.RevokedTokens.Add(new RevokedToken(jti, subject, expiresAtUtc, reason));
        await _db.SaveChangesAsync(ct);
        await _audit.AppendAsync(AuditKind.TokenRevoked, subject, "revoke_token", jti, "-", "-", "-", reason, true, ct);
    }

    private async Task<TokenResponse> IssueUserAsync(TokenRequest r, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Subject == r.Subject || u.Email == r.Subject, ct)
            ?? throw new UnauthorizedAccessException("invalid_credentials");
        if (!SecretHasher.Verify(r.Password ?? "", user.PasswordHash, user.PasswordSalt))
            throw new UnauthorizedAccessException("invalid_credentials");

        var audience = r.Audience ?? _options.DefaultAudience;
        var isAdmin = string.Equals(audience, Audience.Admin, StringComparison.Ordinal);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Subject),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("name", user.DisplayName),
            new("typ", "user"),
        };
        foreach (var role in user.RoleList()) claims.Add(new Claim("roles", role));
        var scopes = NarrowScope(r.Scope, isAdmin ? new[] { Scope.AdminAudit, Scope.AdminUsers, Scope.AdminAuthzEvaluate, Scope.CustomerRead, Scope.CustomerWrite } : new[] { Scope.CustomerRead, Scope.CustomerWrite });
        if (scopes.Length > 0) claims.Add(new Claim("scope", string.Join(' ', scopes)));

        if (isAdmin)
        {
            var amr = r.Amr ?? (user.MfaEnrolled ? "mfa" : "pwd");
            var acr = r.Acr ?? (user.MfaEnrolled ? _options.RequiredAcr : "urn:ntsf:acr:pwd");
            claims.Add(new Claim("amr", amr));
            claims.Add(new Claim("acr", acr));
        }

        var lifetime = TimeSpan.FromMinutes(isAdmin ? _options.AdminAccessTokenMinutes : _options.AccessTokenMinutes);
        var access = await BuildJwtAsync(claims, audience, lifetime, ct);
        var refresh = await CreateFamilyRefreshAsync(user.Subject, audience, string.Join(' ', scopes), ct);

        await _audit.AppendAsync(AuditKind.TokenIssued, user.Subject, "issue_password", audience, "-", "-", "-",
            $"scopes={string.Join(' ', scopes)};kid={access.Kid}", true, ct);
        return access with { RefreshToken = refresh };
    }

    private async Task<TokenResponse> IssueClientCredentialsAsync(TokenRequest r, CancellationToken ct)
    {
        var partner = await _db.Partners.FirstOrDefaultAsync(p => p.ClientId == r.ClientId && p.Enabled, ct)
            ?? throw new UnauthorizedAccessException("invalid_client");
        if (!SecretHasher.Verify(r.ClientSecret ?? "", partner.ClientSecretHash, partner.ClientSecretSalt))
            throw new UnauthorizedAccessException("invalid_client");

        var audience = r.Audience ?? Audience.Partner;
        var scopes = NarrowScope(r.Scope, partner.AllowedScopeList().ToArray());
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, partner.ClientId),
            new("typ", "client"),
            new("partner_code", partner.PartnerCode),
            new("client_id", partner.ClientId),
        };
        if (scopes.Length > 0) claims.Add(new Claim("scope", string.Join(' ', scopes)));

        var access = await BuildJwtAsync(claims, audience, TimeSpan.FromMinutes(_options.AccessTokenMinutes), ct);
        await _audit.AppendAsync(AuditKind.TokenIssued, partner.ClientId, "issue_client_credentials", audience, "-", "-", "-",
            $"partner={partner.PartnerCode};scopes={string.Join(' ', scopes)}", true, ct);
        return access;
    }

    private async Task<TokenResponse> IssueServiceAsync(TokenRequest r, CancellationToken ct)
    {
        var subject = r.Subject ?? "spiffe://demo/ns/default/sa/anonymous";
        var audience = r.Audience ?? Audience.InternalService;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new("typ", "service"),
            new("spiffe_id", subject),
        };
        var scopes = NarrowScope(r.Scope, new[] { Scope.InternalServiceBilling });
        if (scopes.Length > 0) claims.Add(new Claim("scope", string.Join(' ', scopes)));
        var access = await BuildJwtAsync(claims, audience, TimeSpan.FromMinutes(_options.AccessTokenMinutes), ct);
        await _audit.AppendAsync(AuditKind.ServiceTokenIssued, subject, "issue_service", audience, "-", "-", "-",
            $"scopes={string.Join(' ', scopes)}", true, ct);
        return access;
    }

    private async Task<TokenResponse> RefreshAsync(TokenRequest r, CancellationToken ct)
    {
        var raw = r.RefreshToken ?? throw new UnauthorizedAccessException("invalid_grant");
        var hash = SecretHasher.Sha256Hex(raw);
        var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new UnauthorizedAccessException("invalid_grant");
        var now = _clock.UtcNow;

        if (stored.Consumed || stored.Revoked)
        {
            // Reuse detection: theft signal — revoke the whole family.
            var family = await _db.RefreshTokens.Where(t => t.FamilyId == stored.FamilyId).ToListAsync(ct);
            foreach (var f in family) f.Revoke();
            await _db.SaveChangesAsync(ct);
            await _audit.AppendAsync(AuditKind.RefreshReuseDetected, stored.Subject, "refresh_reuse",
                stored.FamilyId.ToString(), "-", "-", "-",
                $"family {stored.FamilyId} revoked", false, ct);
            throw new UnauthorizedAccessException("refresh_reuse_detected");
        }

        if (!stored.IsActive(now))
        {
            throw new UnauthorizedAccessException("invalid_grant");
        }

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, stored.Subject),
            new("typ", "user"),
        };
        if (!string.IsNullOrEmpty(stored.Scopes))
            claims.Add(new Claim("scope", stored.Scopes));

        var access = await BuildJwtAsync(claims, stored.Audience,
            TimeSpan.FromMinutes(_options.AccessTokenMinutes), ct);

        var newRaw = GenerateOpaque();
        var newHash = SecretHasher.Sha256Hex(newRaw);
        var newToken = new RefreshToken(newHash, stored.FamilyId, stored.Subject, stored.Audience, stored.Scopes,
            now.AddDays(_options.RefreshTokenDays));
        _db.RefreshTokens.Add(newToken);
        await _db.SaveChangesAsync(ct);
        stored.MarkConsumed(newToken.Id);
        await _db.SaveChangesAsync(ct);
        await _audit.AppendAsync(AuditKind.TokenRefreshed, stored.Subject, "refresh_token",
            stored.FamilyId.ToString(), "-", "-", "-", $"kid={access.Kid}", true, ct);
        return access with { RefreshToken = newRaw };
    }

    private static string[] NarrowScope(string? requested, string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(requested)) return allowed;
        var requestedList = requested.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return requestedList.Where(s => allowed.Contains(s)).ToArray();
    }

    private async Task<string> CreateFamilyRefreshAsync(string subject, string audience, string scopes, CancellationToken ct)
    {
        var raw = GenerateOpaque();
        var hash = SecretHasher.Sha256Hex(raw);
        var token = new RefreshToken(hash, Guid.NewGuid(), subject, audience, scopes,
            _clock.UtcNow.AddDays(_options.RefreshTokenDays));
        _db.RefreshTokens.Add(token);
        await _db.SaveChangesAsync(ct);
        return raw;
    }

    private static string GenerateOpaque()
    {
        return Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));
    }

    private async Task<TokenResponse> BuildJwtAsync(IEnumerable<Claim> claims, string audience, TimeSpan lifetime, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var exp = now.Add(lifetime);
        var jti = Guid.NewGuid().ToString("N");
        var allClaims = new List<Claim>(claims)
        {
            new(JwtRegisteredClaimNames.Jti, jti),
            new(JwtRegisteredClaimNames.Iat, ToUnix(now), ClaimValueTypes.Integer64),
        };

        SigningCredentials creds;
        string kid;
        string alg;
        if (_options.UseRs256)
        {
            var key = await _db.SigningKeys
                .Where(k => k.IsPrimary && k.RetiredAtUtc == null && k.NotBeforeUtc <= now)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("No active RS256 signing key");
            var rsa = RSA.Create();
            rsa.ImportFromPem(key.PrivateKeyPem);
            var rsaKey = new RsaSecurityKey(rsa) { KeyId = key.Kid };
            creds = new SigningCredentials(rsaKey, SecurityAlgorithms.RsaSha256);
            kid = key.Kid;
            alg = "RS256";
        }
        else
        {
            var hs = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.HsSigningKey)) { KeyId = "hs-default" };
            creds = new SigningCredentials(hs, SecurityAlgorithms.HmacSha256);
            kid = "hs-default";
            alg = "HS256";
        }

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: audience,
            claims: allClaims,
            notBefore: now,
            expires: exp,
            signingCredentials: creds);
        var jwt = new JwtSecurityTokenHandler().WriteToken(token);
        return new TokenResponse(jwt, "Bearer", (int)lifetime.TotalSeconds,
            allClaims.FirstOrDefault(c => c.Type == "scope")?.Value ?? string.Empty, null, kid, alg);
    }

    private static string ToUnix(DateTime utc) =>
        new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
