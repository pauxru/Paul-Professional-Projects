using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Api.Endpoints;
using ZeroTrust.Domain.Audit;
using ZeroTrust.Domain.Identity;
using ZeroTrust.Infrastructure.Persistence;
using ZeroTrust.Infrastructure.Security;

namespace ZeroTrust.Api.Authorization;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "ApiKey";
    public string HeaderName { get; set; } = "X-Api-Key";
    public bool EnforcementActive { get; set; } = false;
}

/// <summary>
/// Legacy API key authentication (salted PBKDF2 stored). The header value is `keyId:secret`.
/// During the dual-accept migration window, both API keys and JWTs are accepted on the
/// partner surface. After EnforcementActive=true and the key's DeprecatedAfterUtc has passed,
/// the key is rejected.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly ZeroTrustDbContext _db;
    private readonly IAuditLog _audit;
    private readonly Application.Abstractions.IClock _clock;
    private readonly IApiKeyToggle _toggle;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ZeroTrustDbContext db,
        IAuditLog audit,
        Application.Abstractions.IClock clock,
        IApiKeyToggle toggle)
        : base(options, logger, encoder)
    {
        _db = db;
        _audit = audit;
        _clock = clock;
        _toggle = toggle;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var raw = Request.Headers[Options.HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return AuthenticateResult.NoResult();

        var parts = raw.Split(':', 2);
        if (parts.Length != 2) return AuthenticateResult.Fail("bad_api_key_format");
        var keyId = parts[0];
        var secret = parts[1];
        var record = await _db.ApiKeys.FirstOrDefaultAsync(x => x.KeyId == keyId);
        if (record is null) return AuthenticateResult.Fail("api_key_unknown");

        var now = _clock.UtcNow;
        if (record.Revoked) return AuthenticateResult.Fail("api_key_revoked");

        var enforcement = _toggle.EnforcementActive || Options.EnforcementActive;
        if (enforcement && record.DeprecatedAfterUtc is not null && record.DeprecatedAfterUtc <= now)
        {
            await _audit.AppendAsync(AuditKind.ApiKeyRejectedPostCutover, keyId, "api_key_auth",
                Request.Path, "-", Request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "-",
                Request.Headers.UserAgent.ToString(), "past_deprecation", false, Request.HttpContext.RequestAborted);
            return AuthenticateResult.Fail("api_key_past_cutover");
        }

        if (!SecretHasher.Verify(secret, record.KeyHash, record.KeySalt))
            return AuthenticateResult.Fail("api_key_invalid_secret");

        record.RecordUse(now);
        await _db.SaveChangesAsync();

        var scopes = record.AllowedScopes;
        var claims = new List<Claim>
        {
            new("sub", $"apikey:{keyId}"),
            new("typ", "apikey"),
            new("partner_code", record.OwnerPartnerCode),
            new("scope", scopes),
            new("aud", Audience.Partner),
        };
        var deprecated = record.DeprecatedAfterUtc is not null && record.DeprecatedAfterUtc <= now;
        if (deprecated)
        {
            await _audit.AppendAsync(AuditKind.ApiKeyDeprecatedUsed, keyId, "api_key_auth", Request.Path,
                "-", Request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "-",
                Request.Headers.UserAgent.ToString(),
                "used past deprecation but enforcement disabled", true, Request.HttpContext.RequestAborted);
        }
        else
        {
            await _audit.AppendAsync(AuditKind.ApiKeyUsed, keyId, "api_key_auth", Request.Path,
                "-", Request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "-",
                Request.Headers.UserAgent.ToString(), "ok", true, Request.HttpContext.RequestAborted);
        }

        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationOptions.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, ApiKeyAuthenticationOptions.SchemeName));
    }
}
