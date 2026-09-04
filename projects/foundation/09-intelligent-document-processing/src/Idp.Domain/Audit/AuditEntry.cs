namespace Idp.Domain.Audit;

/// <summary>
/// Append-only audit record for security-relevant actions. Never mutated or deleted by application
/// code. Captures actor, action, resource, correlation id and a hash of the before/after state.
/// </summary>
public sealed class AuditEntry
{
    public Guid Id { get; private set; }
    public string Actor { get; private set; } = default!;
    public string Action { get; private set; } = default!;
    public string Resource { get; private set; } = default!;
    public string CorrelationId { get; private set; } = default!;
    public DateTime TimestampUtc { get; private set; }
    public string? BeforeHash { get; private set; }
    public string? AfterHash { get; private set; }
    public string? Detail { get; private set; }

    private AuditEntry() { }

    public AuditEntry(
        string actor, string action, string resource, string correlationId, DateTime nowUtc,
        string? beforeHash = null, string? afterHash = null, string? detail = null)
    {
        Id = Guid.NewGuid();
        Actor = actor;
        Action = action;
        Resource = resource;
        CorrelationId = correlationId;
        TimestampUtc = nowUtc;
        BeforeHash = beforeHash;
        AfterHash = afterHash;
        Detail = detail;
    }
}
