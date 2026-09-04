using Healthcare.Application.Abstractions;
using Healthcare.Domain.Appointments;
using Healthcare.Domain.Clinicians;
using Healthcare.Domain.Facilities;
using Microsoft.EntityFrameworkCore;

namespace Healthcare.Application.Availability;

public sealed record SlotSearchQuery(
    Guid FacilityId,
    Guid ClinicianId,
    Guid AppointmentTypeId,
    DateOnly FromLocalDate,
    DateOnly ToLocalDate);

public sealed record AvailableSlot(
    Guid FacilityId,
    Guid ClinicianId,
    Guid RoomId,
    Guid AppointmentTypeId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string FacilityTimeZoneId,
    string LocalStartIso);

/// <summary>
/// Slot generation engine. Composes: facility operating hours &amp; closures, clinician working
/// pattern &amp; leave, appointment type duration+buffer, room capabilities, existing bookings.
/// Timezone-correct across DST transitions by anchoring in the facility's local time zone.
/// </summary>
public sealed class SlotEngine
{
    private readonly IAppDbContext _db;
    public SlotEngine(IAppDbContext db) => _db = db;

    public async Task<IReadOnlyList<AvailableSlot>> SearchAsync(SlotSearchQuery query, CancellationToken ct)
    {
        var facility = await _db.Facilities.FirstOrDefaultAsync(f => f.Id == query.FacilityId, ct)
            ?? throw new InvalidOperationException("Facility not found.");
        var apptType = await _db.AppointmentTypes.FirstOrDefaultAsync(a => a.Id == query.AppointmentTypeId, ct)
            ?? throw new InvalidOperationException("Appointment type not found.");
        var clinician = await _db.Clinicians.FirstOrDefaultAsync(c => c.Id == query.ClinicianId, ct)
            ?? throw new InvalidOperationException("Clinician not found.");
        // Load related sets (small demo db — a real system would materialise via joins).
        var facilityRooms = await _db.Rooms.Where(r => r.FacilityId == facility.Id).ToListAsync(ct);
        var hours = await _db.OperatingHours.Where(h => h.FacilityId == facility.Id).ToListAsync(ct);
        var closures = await _db.Closures.Where(c => c.FacilityId == facility.Id).ToListAsync(ct);
        var patterns = await _db.WorkingPatterns.Where(p => p.ClinicianId == clinician.Id && p.FacilityId == facility.Id).ToListAsync(ct);
        var leaves = await _db.LeavePeriods.Where(l => l.ClinicianId == clinician.Id).ToListAsync(ct);
        var existing = await _db.Appointments
            .Where(a => a.FacilityId == facility.Id
                     && a.Status != AppointmentStatus.Cancelled
                     && a.Status != AppointmentStatus.NoShow)
            .ToListAsync(ct);

        var tz = TimeZoneInfo.FindSystemTimeZoneById(facility.TimeZoneId);
        var slotLenMinutes = apptType.DurationMinutes;
        var buffer = TimeSpan.FromMinutes(apptType.BufferMinutes);
        var minBreak = TimeSpan.FromMinutes(clinician.MinBreakBetweenMinutes);

        // Rooms eligible for this appointment type
        var eligibleRooms = facilityRooms
            .Where(r => string.IsNullOrEmpty(apptType.RequiredRoomCapability) || r.HasCapability(apptType.RequiredRoomCapability))
            .ToList();

        var results = new List<AvailableSlot>();

        for (var d = query.FromLocalDate; d <= query.ToLocalDate; d = d.AddDays(1))
        {
            if (closures.Any(c => c.Date == d)) continue;
            var facHours = hours.FirstOrDefault(h => h.Day == d.DayOfWeek);
            if (facHours is null) continue;
            var pattern = patterns.FirstOrDefault(p => p.Day == d.DayOfWeek);
            if (pattern is null) continue;
            if (leaves.Any(l => l.StartDate <= d && d <= l.EndDate)) continue;

            // Window is intersection of facility open hours and clinician working pattern.
            var winOpen = facHours.Open > pattern.Start ? facHours.Open : pattern.Start;
            var winClose = facHours.Close < pattern.End ? facHours.Close : pattern.End;
            if (winClose <= winOpen) continue;

            // Enumerate slots at (duration+buffer) cadence.
            var cursor = winOpen;
            while (true)
            {
                var slotEnd = cursor.AddMinutes(slotLenMinutes);
                if (slotEnd > winClose) break;

                // Convert local to UTC (DST-correct)
                var localStart = new DateTime(d.Year, d.Month, d.Day, cursor.Hour, cursor.Minute, 0, DateTimeKind.Unspecified);
                var localEnd = localStart.AddMinutes(slotLenMinutes);
                DateTimeOffset startUtc, endUtc;
                try
                {
                    if (tz.IsInvalidTime(localStart) || tz.IsInvalidTime(localEnd))
                    {
                        cursor = cursor.AddMinutes(slotLenMinutes + apptType.BufferMinutes);
                        continue;
                    }
                    startUtc = new DateTimeOffset(localStart, tz.GetUtcOffset(localStart));
                    endUtc = new DateTimeOffset(localEnd, tz.GetUtcOffset(localEnd));
                }
                catch
                {
                    cursor = cursor.AddMinutes(slotLenMinutes + apptType.BufferMinutes);
                    continue;
                }

                // Find a room that is free for this slot (including buffer).
                Guid? freeRoomId = null;
                foreach (var room in eligibleRooms)
                {
                    var roomTaken = existing.Any(a => a.RoomId == room.Id && Overlaps(a.StartUtc, a.EndUtc, startUtc, endUtc, buffer));
                    if (!roomTaken) { freeRoomId = room.Id; break; }
                }
                if (freeRoomId is null)
                {
                    cursor = cursor.AddMinutes(slotLenMinutes + apptType.BufferMinutes);
                    continue;
                }

                // Check clinician availability including min break.
                var clinicianTaken = existing.Any(a => a.ClinicianId == clinician.Id && Overlaps(a.StartUtc, a.EndUtc, startUtc, endUtc, minBreak));
                if (clinicianTaken)
                {
                    cursor = cursor.AddMinutes(slotLenMinutes + apptType.BufferMinutes);
                    continue;
                }

                results.Add(new AvailableSlot(
                    facility.Id, clinician.Id, freeRoomId.Value, apptType.Id,
                    startUtc, endUtc, facility.TimeZoneId, localStart.ToString("s")));

                cursor = cursor.AddMinutes(slotLenMinutes + apptType.BufferMinutes);
            }
        }
        return results;
    }

    private static bool Overlaps(DateTimeOffset aStart, DateTimeOffset aEnd, DateTimeOffset bStart, DateTimeOffset bEnd, TimeSpan padding) =>
        aStart - padding < bEnd && bStart < aEnd + padding;
}
