namespace Contoso.Payments.Domain.Audit;

/// <summary>
/// Append-only audit row.  Every state-changing operation writes one of these.  Rows carry
/// hashes of the before/after state — the payload itself is not persisted to keep the audit
/// trail size bounded and to avoid leaking PII into a general-purpose log.
/// </summary>
public sealed class AuditEvent
{
    private AuditEvent()
    {
        Actor = string.Empty;
        Action = string.Empty;
        Resource = string.Empty;
        CorrelationId = string.Empty;
        BeforeHash = string.Empty;
        AfterHash = string.Empty;
    }

    public AuditEvent(Guid id, string actor, string action, string resource,
        string correlationId, string beforeHash, string afterHash, DateTimeOffset atUtc)
    {
        Id = id;
        Actor = actor;
        Action = action;
        Resource = resource;
        CorrelationId = correlationId;
        BeforeHash = beforeHash;
        AfterHash = afterHash;
        AtUtc = atUtc;
    }

    public Guid Id { get; private set; }
    public string Actor { get; private set; }
    public string Action { get; private set; }
    public string Resource { get; private set; }
    public string CorrelationId { get; private set; }
    public string BeforeHash { get; private set; }
    public string AfterHash { get; private set; }
    public DateTimeOffset AtUtc { get; private set; }
}
