using Healthcare.Application.Abstractions;
using Healthcare.Domain.Appointments;
using Microsoft.EntityFrameworkCore;

namespace Healthcare.Application.Reports;

public sealed record ClinicBoardEntry(
    Guid AppointmentId,
    string PatientExternalId,
    string PatientGivenName,
    string PatientFamilyName,
    Guid ClinicianId,
    string ClinicianName,
    string Status,
    DateTimeOffset StartUtc,
    int? WaitMinutes);

public sealed record UtilisationRow(Guid ClinicianId, string ClinicianName, int TotalMinutes,
    int BookedMinutes, double UtilisationPct, int PatientCount);

public sealed record DnaRow(Guid ClinicianId, string ClinicianName, int Booked, int NoShow, double DnaPct);

public sealed record AnomalyRow(string ActorId, string ActorRole, string? PatientExternalId,
    string Reason, DateTimeOffset OccurredAt);

public sealed class ReportsService
{
    private readonly IAppDbContext _db;
    public ReportsService(IAppDbContext db) => _db = db;

    public async Task<IReadOnlyList<ClinicBoardEntry>> ClinicBoardAsync(Guid facilityId, DateOnly localDate,
        CancellationToken ct)
    {
        var facility = await _db.Facilities.FirstOrDefaultAsync(f => f.Id == facilityId, ct);
        if (facility is null) return Array.Empty<ClinicBoardEntry>();
        var tz = TimeZoneInfo.FindSystemTimeZoneById(facility.TimeZoneId);
        var startLocal = new DateTime(localDate.Year, localDate.Month, localDate.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocal = startLocal.AddDays(1);
        var startUtc = new DateTimeOffset(startLocal, tz.GetUtcOffset(startLocal)).ToUniversalTime();
        var endUtc = new DateTimeOffset(endLocal, tz.GetUtcOffset(endLocal)).ToUniversalTime();

        var appts = await _db.Appointments
            .Where(a => a.FacilityId == facilityId && a.StartUtc >= startUtc && a.StartUtc < endUtc)
            .OrderBy(a => a.StartUtc)
            .ToListAsync(ct);

        var patients = await _db.Patients
            .Where(p => appts.Select(a => a.PatientId).Contains(p.Id))
            .ToListAsync(ct);
        var clinicians = await _db.Clinicians
            .Where(c => appts.Select(a => a.ClinicianId).Contains(c.Id))
            .ToListAsync(ct);

        var result = new List<ClinicBoardEntry>();
        foreach (var a in appts)
        {
            var p = patients.FirstOrDefault(x => x.Id == a.PatientId);
            var c = clinicians.FirstOrDefault(x => x.Id == a.ClinicianId);
            int? wait = null;
            if (a.CheckedInAt is not null && a.WithClinicianAt is not null)
                wait = (int)(a.WithClinicianAt.Value - a.CheckedInAt.Value).TotalMinutes;
            else if (a.CheckedInAt is not null)
                wait = (int)(DateTimeOffset.UtcNow - a.CheckedInAt.Value).TotalMinutes;
            result.Add(new ClinicBoardEntry(
                a.Id,
                p?.ExternalId.Value ?? "?",
                p?.GivenName ?? "?",
                p?.FamilyName ?? "?",
                a.ClinicianId,
                c is null ? "?" : $"{c.GivenName} {c.FamilyName}",
                a.Status.ToString(),
                a.StartUtc,
                wait));
        }
        return result;
    }

    public async Task<IReadOnlyList<UtilisationRow>> UtilisationAsync(Guid facilityId, DateOnly fromLocal,
        DateOnly toLocal, CancellationToken ct)
    {
        var facility = await _db.Facilities.FirstOrDefaultAsync(f => f.Id == facilityId, ct);
        if (facility is null) return Array.Empty<UtilisationRow>();
        var tz = TimeZoneInfo.FindSystemTimeZoneById(facility.TimeZoneId);
        var startUtc = new DateTimeOffset(fromLocal.ToDateTime(TimeOnly.MinValue), tz.GetUtcOffset(fromLocal.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var endUtc = new DateTimeOffset(toLocal.AddDays(1).ToDateTime(TimeOnly.MinValue), tz.GetUtcOffset(toLocal.AddDays(1).ToDateTime(TimeOnly.MinValue))).ToUniversalTime();

        var appts = await _db.Appointments
            .Where(a => a.FacilityId == facilityId && a.StartUtc >= startUtc && a.StartUtc < endUtc
                && a.Status != Healthcare.Domain.Appointments.AppointmentStatus.Cancelled)
            .ToListAsync(ct);
        var clinicians = await _db.Clinicians.ToListAsync(ct);
        var patterns = await _db.WorkingPatterns
            .Where(p => p.FacilityId == facilityId)
            .ToListAsync(ct);

        var result = new List<UtilisationRow>();
        foreach (var c in clinicians)
        {
            var mine = appts.Where(a => a.ClinicianId == c.Id).ToList();
            var patternsMine = patterns.Where(p => p.ClinicianId == c.Id).ToList();
            int totalMinutes = 0;
            for (var d = fromLocal; d <= toLocal; d = d.AddDays(1))
            {
                var pat = patternsMine.FirstOrDefault(p => p.Day == d.DayOfWeek);
                if (pat is not null) totalMinutes += (int)(pat.End - pat.Start).TotalMinutes;
            }
            var booked = (int)mine.Sum(a => (a.EndUtc - a.StartUtc).TotalMinutes);
            var util = totalMinutes == 0 ? 0.0 : (double)booked / totalMinutes * 100.0;
            result.Add(new UtilisationRow(c.Id, $"{c.GivenName} {c.FamilyName}", totalMinutes, booked, Math.Round(util, 1), mine.Count));
        }
        return result;
    }

    public async Task<IReadOnlyList<DnaRow>> DnaStatsAsync(Guid facilityId, DateOnly fromLocal, DateOnly toLocal,
        CancellationToken ct)
    {
        var facility = await _db.Facilities.FirstOrDefaultAsync(f => f.Id == facilityId, ct);
        if (facility is null) return Array.Empty<DnaRow>();
        var tz = TimeZoneInfo.FindSystemTimeZoneById(facility.TimeZoneId);
        var startUtc = new DateTimeOffset(fromLocal.ToDateTime(TimeOnly.MinValue), tz.GetUtcOffset(fromLocal.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var endUtc = new DateTimeOffset(toLocal.AddDays(1).ToDateTime(TimeOnly.MinValue), tz.GetUtcOffset(toLocal.AddDays(1).ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var appts = await _db.Appointments
            .Where(a => a.FacilityId == facilityId && a.StartUtc >= startUtc && a.StartUtc < endUtc)
            .ToListAsync(ct);
        var clinicians = await _db.Clinicians.ToListAsync(ct);
        var rows = new List<DnaRow>();
        foreach (var c in clinicians)
        {
            var mine = appts.Where(a => a.ClinicianId == c.Id).ToList();
            var booked = mine.Count;
            var noShow = mine.Count(a => a.Status == AppointmentStatus.NoShow);
            var pct = booked == 0 ? 0.0 : (double)noShow / booked * 100.0;
            rows.Add(new DnaRow(c.Id, $"{c.GivenName} {c.FamilyName}", booked, noShow, Math.Round(pct, 1)));
        }
        return rows;
    }

    public async Task<IReadOnlyList<AnomalyRow>> AccessAnomaliesAsync(DateTimeOffset? from, CancellationToken ct)
    {
        var since = from ?? DateTimeOffset.UtcNow.AddDays(-30);
        var events = await _db.AuditEvents
            .Where(a => a.OccurredAtUtc >= since &&
                (a.BreakGlass || a.OutOfHours || a.VipPatient ||
                 a.Kind == Healthcare.Domain.Audit.AuditKind.AccessDenied))
            .OrderByDescending(a => a.OccurredAtUtc)
            .Take(200)
            .ToListAsync(ct);
        return events.Select(e =>
        {
            var reasons = new List<string>();
            if (e.BreakGlass) reasons.Add("break_glass");
            if (e.OutOfHours) reasons.Add("out_of_hours");
            if (e.VipPatient) reasons.Add("vip_patient");
            if (e.Kind == Healthcare.Domain.Audit.AuditKind.AccessDenied) reasons.Add("access_denied");
            return new AnomalyRow(e.ActorId, e.ActorRole, e.PatientId, string.Join(",", reasons), e.OccurredAtUtc);
        }).ToList();
    }
}
