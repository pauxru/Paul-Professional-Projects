using System.Security.Claims;
using System.Text;
using AgentPlatform.Application.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AgentPlatform.Api.Security;

/// <summary>Bound from the <c>Jwt</c> configuration section. Dev defaults are clearly non-secret.</summary>
public sealed class JwtOptions
{
    public string Issuer { get; set; } = "agent-platform";
    public string Audience { get; set; } = "agent-platform-clients";

    /// <summary>HMAC signing key. MUST be overridden in any real deployment (see .env.example).</summary>
    public string SigningKey { get; set; } = "dev-only-insecure-signing-key-change-me-please-32b";

    /// <summary>Whether to expose the local dev-token mint endpoint. Off unless explicitly enabled.</summary>
    public bool EnableDevTokenEndpoint { get; set; } = true;
}

/// <summary>Authorisation policy + claim-type constants used across the API.</summary>
public static class AgentPolicies
{
    public const string Run = "agents:run";
    public const string Approve = "agents:approve";
    public const string Admin = "agents:admin";

    public const string ScopeClaim = "scope";
    public const string TenantClaim = "tenant";
    public const string SubjectClaim = "sub";

    public static readonly string[] All = { Run, Approve, Admin };
}

/// <summary>Mints and interprets the platform's JWTs. Symmetric HS256 so it works fully offline.</summary>
public sealed class TokenService
{
    private readonly JwtOptions _options;
    private readonly SymmetricSecurityKey _key;

    public TokenService(JwtOptions options)
    {
        _options = options;
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
    }

    public SymmetricSecurityKey SigningKey => _key;
    public string Issuer => _options.Issuer;
    public string Audience => _options.Audience;

    public string Issue(string subject, string tenant, IEnumerable<string> scopes, TimeSpan lifetime)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(lifetime),
            Claims = new Dictionary<string, object>
            {
                [AgentPolicies.SubjectClaim] = subject,
                [AgentPolicies.TenantClaim] = tenant,
                [AgentPolicies.ScopeClaim] = string.Join(' ', scopes),
            },
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}

/// <summary>Builds the platform's <see cref="AgentCaller"/> from the authenticated principal.</summary>
public static class CallerFactory
{
    public static AgentCaller FromPrincipal(ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue(AgentPolicies.SubjectClaim)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? "anonymous";
        var tenant = principal.FindFirstValue(AgentPolicies.TenantClaim) ?? "default";
        var scopes = ScopesOf(principal);
        return new AgentCaller(userId, tenant, scopes);
    }

    public static IReadOnlySet<string> ScopesOf(ClaimsPrincipal principal)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in principal.FindAll(AgentPolicies.ScopeClaim))
            foreach (var scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(scope);
        return set;
    }
}
