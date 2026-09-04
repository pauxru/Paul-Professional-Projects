namespace ReconEngine.Api.Auth;

/// <summary>Bound from the <c>Jwt</c> configuration section.</summary>
public sealed class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "recon-engine";
    public string Audience { get; set; } = "recon-engine-clients";
    public string Key { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 60;
}

/// <summary>The authorization scopes and the policy names that gate the privileged endpoints.</summary>
public static class ReconScopes
{
    public const string Run = "recon:run";
    public const string Resolve = "recon:resolve";
    public const string Approve = "recon:approve";

    public static readonly string[] All = { Run, Resolve, Approve };
}
