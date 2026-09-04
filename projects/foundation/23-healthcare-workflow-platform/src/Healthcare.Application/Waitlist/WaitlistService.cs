using Healthcare.Application.Abstractions;
using Healthcare.Application.Appointments;
using Healthcare.Domain.Waitlist;
using Microsoft.EntityFrameworkCore;

namespace Healthcare.Application.Waitlist;

public sealed class WaitlistService
{
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    /// <summary>How long a waitlist offer remains open before it auto-expires.</summary>
    public static readonly TimeSpan AcceptanceWindow = TimeSpan.FromHours(4);

    public WaitlistService(IAppDbContext db, IClock clock, IAuditService audit)
    { _db = db; _clock = clock; _audit = audit; }

    public async Task<WaitlistEntry> JoinAsync(Guid patientId, Guid facilityId, Guid appointmentTypeId,
        WaitlistPriority priority, string actorId, string actorRole, string correlationId,
        CancellationToken ct)
    {
        var entry = WaitlistEntry.Create(patientId, facilityId, appointmentTypeId, priority, _clock.UtcNow);
        _db.WaitlistEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
        var pat = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId, ct);
        await _audit.RecordAsync(new AuditRequest(
            Healthcare.Domain.Audit.AuditKind.PatientDataWrite, actorId, actorRole,
            pat?.ExternalId.Value, $"waitlist:{entry.Id}", "join",
            "waitlist.join", correlationId, false, null, pat?.IsVip ?? false), ct);
        return entry;
    }

    /// <summary>
    /// Called after a slot frees up (cancellation or no-show). Picks the highest-priority active
    /// waitlist entry for the same appointment type at the same facility and marks it Offered.
    /// Returns the offered entry, or null if nothing to offer.
    /// </summary>
    public async Task<WaitlistEntry?> OfferSlotAsync(Guid facilityId, Guid appointmentTypeId, Guid appointmentId,
        CancellationToken ct)
    {
        var candidates = await _db.WaitlistEntries
            .Where(w => w.FacilityId == facilityId
                     && w.AppointmentTypeId == appointmentTypeId
                     && w.Status == WaitlistStatus.Active)
            .OrderByDescending(w => (int)w.Priority)
            .ThenBy(w => w.CreatedAtUtc)
            .ToListAsync(ct);
        var entry = candidates.FirstOrDefault();
        if (entry is null) return null;
        entry.OfferSlot(appointmentId, AcceptanceWindow, _clock.UtcNow);
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<int> ExpireStaleOffersAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var toExpire = await _db.WaitlistEntries
            .Where(w => w.Status == WaitlistStatus.Offered && w.OfferExpiresAtUtc < now)
            .ToListAsync(ct);
        foreach (var e in toExpire) e.Expire(now);
        await _db.SaveChangesAsync(ct);
        return toExpire.Count;
    }
}
