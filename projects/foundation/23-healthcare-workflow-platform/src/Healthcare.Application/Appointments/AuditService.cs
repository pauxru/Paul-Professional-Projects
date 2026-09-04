using Healthcare.Application.Abstractions;
using Healthcare.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace Healthcare.Application.Appointments;

public sealed record AuditRequest(
    AuditKind Kind,
    string ActorId,
    string ActorRole,
    string? PatientId,
    string Resource,
    string Action,
    string Purpose,
    string CorrelationId,
    bool BreakGlass,
    string? Justification,
    bool VipPatient);

public interface IAuditService
{
    Task RecordAsync(AuditRequest req, CancellationToken ct);
    Task<IReadOnlyList<AuditEvent>> QueryAsync(DateTimeOffset? from, DateTimeOffset? to, string? actorId,
        string? patientId, AuditKind? kind, CancellationToken ct);
}

public sealed class AuditService : IAuditService
{
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    /// <summary>Local hours considered "in hours" for anomaly detection (facility-agnostic default).</summary>
    private const int InHoursStart = 7;
    private const int InHoursEnd = 20;

    public AuditService(IAppDbContext db, IClock clock) { _db = db; _clock = clock; }

    public async Task RecordAsync(AuditRequest req, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var localHour = now.LocalDateTime.Hour;
        var outOfHours = localHour < InHoursStart || localHour >= InHoursEnd;
        var evt = AuditEvent.Create(
            req.Kind, req.ActorId, req.ActorRole, req.PatientId, req.Resource, req.Action,
            req.Purpose, req.CorrelationId, sourceIp: null, userAgent: null,
            req.BreakGlass, req.Justification, now, outOfHours, req.VipPatient);
        _db.AuditEvents.Add(evt);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> QueryAsync(DateTimeOffset? from, DateTimeOffset? to,
        string? actorId, string? patientId, AuditKind? kind, CancellationToken ct)
    {
        IQueryable<AuditEvent> q = _db.AuditEvents;
        if (from is not null) q = q.Where(a => a.OccurredAtUtc >= from);
        if (to is not null) q = q.Where(a => a.OccurredAtUtc <= to);
        if (!string.IsNullOrWhiteSpace(actorId)) q = q.Where(a => a.ActorId == actorId);
        if (!string.IsNullOrWhiteSpace(patientId)) q = q.Where(a => a.PatientId == patientId);
        if (kind is not null) q = q.Where(a => a.Kind == kind);
        return await q.OrderByDescending(a => a.OccurredAtUtc).Take(500).ToListAsync(ct);
    }
}
