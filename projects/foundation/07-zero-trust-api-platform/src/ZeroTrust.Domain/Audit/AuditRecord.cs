using ZeroTrust.Domain.Common;

namespace ZeroTrust.Domain.Audit;

public enum AuditKind
{
    AuthenticationSuccess,
    AuthenticationFailure,
    AuthorizationAllow,
    AuthorizationDeny,
    TokenIssued,
    TokenRefreshed,
    TokenRevoked,
    RefreshReuseDetected,
    ApiKeyUsed,
    ApiKeyDeprecatedUsed,
    ApiKeyRejectedPostCutover,
    RateLimitTriggered,
    WebhookRejected,
    AdminAction,
    BreakGlassRequested,
    BreakGlassApproved,
    BreakGlassUsed,
    KeyRotated,
    ServiceTokenIssued,
    ServiceTokenRejectedCrossAudience
}

/// <summary>
/// Append-only audit record. `PreviousHash` + `Hash` form a hash chain that makes
/// tampering with historical rows detectable by re-computing the chain.
/// </summary>
public sealed class AuditRecord : Entity
{
    public long Sequence { get; private set; }
    public AuditKind Kind { get; private set; }
    public string Actor { get; private set; } = default!;
    public string Action { get; private set; } = default!;
    public string Resource { get; private set; } = default!;
    public string CorrelationId { get; private set; } = default!;
    public string SourceIp { get; private set; } = string.Empty;
    public string UserAgent { get; private set; } = string.Empty;
    public string Detail { get; private set; } = string.Empty;
    public bool Allowed { get; private set; }
    public string PreviousHash { get; private set; } = default!;
    public string Hash { get; private set; } = default!;

    private AuditRecord() { }

    public AuditRecord(long sequence, AuditKind kind, string actor, string action, string resource,
        string correlationId, string sourceIp, string userAgent, string detail, bool allowed,
        string previousHash, string hash, DateTime nowUtc)
    {
        Sequence = sequence;
        Kind = kind;
        Actor = actor;
        Action = action;
        Resource = resource;
        CorrelationId = correlationId;
        SourceIp = sourceIp;
        UserAgent = userAgent;
        Detail = detail;
        Allowed = allowed;
        PreviousHash = previousHash;
        Hash = hash;
        CreatedAtUtc = nowUtc;
    }
}
