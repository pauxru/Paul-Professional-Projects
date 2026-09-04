using Healthcare.Application.Abstractions;
using Healthcare.Application.Appointments;
using Healthcare.Domain.Common;
using Healthcare.Domain.Referrals;
using Microsoft.EntityFrameworkCore;

namespace Healthcare.Application.Referrals;

public sealed record CreateReferralCommand(
    Guid PatientId,
    Guid ReferringClinicianId,
    Guid? DestinationFacilityId,
    string ExternalDestination,
    string Speciality,
    ReferralPriority Priority,
    string ReasonForReferral);

public sealed class ReferralService
{
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    public ReferralService(IAppDbContext db, IClock clock, IAuditService audit)
    { _db = db; _clock = clock; _audit = audit; }

    public async Task<Referral> CreateAsync(CreateReferralCommand cmd, string actorId, string actorRole,
        string correlationId, CancellationToken ct)
    {
        var referral = Referral.Create(cmd.PatientId, cmd.ReferringClinicianId, cmd.DestinationFacilityId,
            cmd.ExternalDestination, cmd.Speciality, cmd.Priority, cmd.ReasonForReferral, _clock.UtcNow);
        _db.Referrals.Add(referral);
        await _db.SaveChangesAsync(ct);
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == cmd.PatientId, ct);
        await _audit.RecordAsync(new AuditRequest(
            Healthcare.Domain.Audit.AuditKind.PatientDataWrite, actorId, actorRole,
            patient?.ExternalId.Value, $"referral:{referral.Id}", "create",
            "referral.create", correlationId, false, null, patient?.IsVip ?? false), ct);
        return referral;
    }

    public async Task TransitionAsync(Guid referralId, string action, string? reason, string actorId,
        string actorRole, string correlationId, CancellationToken ct)
    {
        var ref_ = await _db.Referrals.FirstOrDefaultAsync(r => r.Id == referralId, ct)
            ?? throw new DomainException("referral.not_found", "Referral not found.");
        var now = _clock.UtcNow;
        switch (action.ToLowerInvariant())
        {
            case "submit": ref_.Submit(now); break;
            case "triage": ref_.Triage(now); break;
            case "accept": ref_.Accept(now); break;
            case "reject": ref_.Reject(reason ?? "unspecified", now); break;
            case "complete": ref_.Complete(now); break;
            default:
                throw new DomainException("referral.action.invalid", $"Unknown referral action '{action}'.");
        }
        await _db.SaveChangesAsync(ct);
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == ref_.PatientId, ct);
        await _audit.RecordAsync(new AuditRequest(
            Healthcare.Domain.Audit.AuditKind.PatientDataWrite, actorId, actorRole,
            patient?.ExternalId.Value, $"referral:{ref_.Id}", action,
            $"referral.{action}", correlationId, false, null, patient?.IsVip ?? false), ct);
    }

    public async Task<int> EvaluateSlasAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var open = await _db.Referrals
            .Where(r => r.Status == ReferralStatus.Draft ||
                        r.Status == ReferralStatus.Submitted ||
                        r.Status == ReferralStatus.Triaged)
            .ToListAsync(ct);
        int newBreaches = 0;
        foreach (var r in open)
        {
            var wasBreached = r.SlaBreached;
            var isBreached = r.EvaluateSla(now);
            if (!wasBreached && isBreached) newBreaches++;
        }
        await _db.SaveChangesAsync(ct);
        return newBreaches;
    }
}
