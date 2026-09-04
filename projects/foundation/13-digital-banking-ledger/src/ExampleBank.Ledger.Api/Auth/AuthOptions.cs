namespace ExampleBank.Ledger.Api.Auth;

/// <summary>Authorization policy names and the scopes they require.</summary>
public static class LedgerPolicies
{
    public const string Read = "ledger:read";
    public const string Post = "ledger:post";
    public const string Adjust = "ledger:adjust";
    public const string Admin = "ledger:admin";

    public static readonly string[] All = { Read, Post, Adjust, Admin };
}

/// <summary>Bound from the <c>Auth</c> configuration section.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Issuer { get; set; } = "ExampleBank.Ledger";
    public string Audience { get; set; } = "ExampleBank.Ledger.Api";

    /// <summary>Symmetric signing key. MUST be overridden in production via <c>Auth__SigningKey</c>.</summary>
    public string SigningKey { get; set; } = "dev-only-insecure-signing-key-please-override-me-01234567890";

    /// <summary>When true, exposes the local <c>/api/v1/dev/token</c> minting endpoint (never enable in prod).</summary>
    public bool EnableDevTokens { get; set; }
}
