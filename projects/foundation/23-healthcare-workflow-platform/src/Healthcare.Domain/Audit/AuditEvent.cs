using Healthcare.Domain.Common;

namespace Healthcare.Domain.Audit;

public enum AuditKind
{
    /// <summary>Any read of clinical / demographic data belonging to a patient.</summary>
    PatientDataRead = 0,
    /// <summary>Any write against patient / appointment / encounter data.</summary>
    PatientDataWrite = 1,
    /// <summary>Break-glass override activated by an actor to gain temporary access.</summary>
    BreakGlassActivated = 2,
    /// <summary>Sign-in / authentication event.</summary>
    Authentication = 3,
    /// <summary>Reminder dispatch attempt (successful or failed).</summary>
    ReminderDispatch = 4,
    /// <summary>Access denied — for anomaly detection reporting.</summary>
    AccessDenied = 5
}

/// <summary>Immutable audit event. Persistence layer rejects UPDATE and DELETE on this table.</summary>
public sealed class AuditEvent : Entity
{
    public AuditKind Kind { get; private set; }
    public string ActorId { get; private set; } = string.Empty;
    public string ActorRole { get; private set; } = string.Empty;
    public string? PatientId { get; private set; }
    public string Resource { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;
    public string Purpose { get; private set; } = string.Empty;
    public string CorrelationId { get; private set; } = string.Empty;
    public string? SourceIp { get; private set; }
    public string? UserAgent { get; private set; }
    public bool BreakGlass { get; private set; }
    public string? Justification { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public bool OutOfHours { get; private set; }
    public bool VipPatient { get; private set; }

    private AuditEvent() { }

    public static AuditEvent Create(AuditKind kind, string actorId, string actorRole, string? patientId,
        string resource, string action, string purpose, string correlationId, string? sourceIp,
        string? userAgent, bool breakGlass, string? justification, DateTimeOffset at, bool outOfHours,
        bool vipPatient)
    {
        if (string.IsNullOrWhiteSpace(actorId)) throw new DomainException("audit.actor.required", "Actor required.");
        if (breakGlass && string.IsNullOrWhiteSpace(justification))
            throw new DomainException("audit.breakglass.justification_required", "Break-glass requires justification.");
        return new AuditEvent
        {
            Kind = kind,
            ActorId = actorId,
            ActorRole = actorRole,
            PatientId = patientId,
            Resource = resource,
            Action = action,
            Purpose = purpose,
            CorrelationId = correlationId,
            SourceIp = sourceIp,
            UserAgent = userAgent,
            BreakGlass = breakGlass,
            Justification = justification,
            OccurredAtUtc = at,
            OutOfHours = outOfHours,
            VipPatient = vipPatient
        };
    }
}
