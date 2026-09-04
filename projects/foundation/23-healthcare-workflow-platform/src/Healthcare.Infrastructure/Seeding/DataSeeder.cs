using Healthcare.Application.Abstractions;
using Healthcare.Domain.Appointments;
using Healthcare.Domain.Clinicians;
using Healthcare.Domain.Facilities;
using Healthcare.Domain.Patients;
using Healthcare.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Healthcare.Infrastructure.Seeding;

/// <summary>
/// Idempotent synthetic data seeder. All names, patient ids and clinic names are labelled as
/// fictional demonstrations — no real personal or health information appears in the seed.
/// </summary>
public static class DataSeeder
{
    public static async Task SeedAsync(AppDbContext db, IClock clock, CancellationToken ct)
    {
        if (await db.Facilities.AnyAsync(ct)) return; // already seeded

        // ---- Facilities ----
        var nairobiCentral = Facility.Create("NBO-CENTRAL", "Nairobi Demo Clinic (fictional)", "Africa/Nairobi");
        for (var d = DayOfWeek.Monday; d <= DayOfWeek.Friday; d++)
            nairobiCentral.SetHours(d, new TimeOnly(8, 0), new TimeOnly(18, 0));
        nairobiCentral.SetHours(DayOfWeek.Saturday, new TimeOnly(9, 0), new TimeOnly(13, 0));

        var lonBranch = Facility.Create("LON-BRANCH", "Demo Health Centre (fictional)", "Europe/London");
        for (var d = DayOfWeek.Monday; d <= DayOfWeek.Friday; d++)
            lonBranch.SetHours(d, new TimeOnly(9, 0), new TimeOnly(17, 30));

        var roomA = nairobiCentral.AddRoom("Consult A", new[] { "general", "gp" });
        var roomB = nairobiCentral.AddRoom("Consult B", new[] { "general", "gp", "gynae" });
        var roomC = nairobiCentral.AddRoom("Procedure 1", new[] { "procedure", "minor_surgery" });
        var roomL1 = lonBranch.AddRoom("Room 1", new[] { "general", "gp" });

        db.Facilities.Add(nairobiCentral);
        db.Facilities.Add(lonBranch);

        // ---- Clinicians ----
        var drA = Clinician.Create("Amina", "Kirui", "General Practitioner",
            new[] { "MBChB (fictional)", "PGDip Family Med (fictional)" });
        drA.AssignToFacility(nairobiCentral.Id);
        drA.SetWorkingPattern(nairobiCentral.Id, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0));
        drA.SetWorkingPattern(nairobiCentral.Id, DayOfWeek.Tuesday, new TimeOnly(9, 0), new TimeOnly(17, 0));
        drA.SetWorkingPattern(nairobiCentral.Id, DayOfWeek.Wednesday, new TimeOnly(9, 0), new TimeOnly(17, 0));
        drA.SetWorkingPattern(nairobiCentral.Id, DayOfWeek.Thursday, new TimeOnly(9, 0), new TimeOnly(17, 0));
        drA.SetWorkingPattern(nairobiCentral.Id, DayOfWeek.Friday, new TimeOnly(9, 0), new TimeOnly(15, 0));

        var drB = Clinician.Create("Brian", "Otieno", "General Practitioner",
            new[] { "MBChB (fictional)" });
        drB.AssignToFacility(nairobiCentral.Id);
        for (var d = DayOfWeek.Monday; d <= DayOfWeek.Friday; d++)
            drB.SetWorkingPattern(nairobiCentral.Id, d, new TimeOnly(10, 0), new TimeOnly(17, 30));

        var drC = Clinician.Create("Catherine", "Njoroge", "Paediatrics",
            new[] { "MBChB (fictional)", "MMed Paeds (fictional)" });
        drC.AssignToFacility(nairobiCentral.Id);
        drC.SetWorkingPattern(nairobiCentral.Id, DayOfWeek.Monday, new TimeOnly(8, 30), new TimeOnly(16, 30));
        drC.SetWorkingPattern(nairobiCentral.Id, DayOfWeek.Wednesday, new TimeOnly(8, 30), new TimeOnly(16, 30));
        drC.SetWorkingPattern(nairobiCentral.Id, DayOfWeek.Friday, new TimeOnly(8, 30), new TimeOnly(16, 30));

        var drL = Clinician.Create("Diana", "Everett", "General Practitioner",
            new[] { "MBBS (fictional)" });
        drL.AssignToFacility(lonBranch.Id);
        for (var d = DayOfWeek.Monday; d <= DayOfWeek.Friday; d++)
            drL.SetWorkingPattern(lonBranch.Id, d, new TimeOnly(9, 30), new TimeOnly(17, 0));

        db.Clinicians.AddRange(drA, drB, drC, drL);

        // ---- Appointment types ----
        var atGp15 = AppointmentType.Create("GP15", "GP Consultation", 15, 5, "gp");
        var atGp30 = AppointmentType.Create("GP30", "GP Extended Consultation", 30, 5, "gp");
        var atMinorProc = AppointmentType.Create("PROC30", "Minor Procedure", 30, 10, "procedure");
        var atPaed = AppointmentType.Create("PAED20", "Paediatrics Consultation", 20, 5, "gp");
        db.AppointmentTypes.AddRange(atGp15, atGp30, atMinorProc, atPaed);

        // ---- Patients (all fictional) ----
        int seq = 1_000_000;
        Patient MakePatient(string given, string family, DateOnly dob, string sex, string phone, string? email, ContactChannel channel, Guid clinic, bool vip = false)
        {
            var id = PatientId.Create(++seq - 1_000_000);
            return Patient.Register(id, given, family, dob, sex, phone, email, channel, clinic, vip);
        }
        var p1 = MakePatient("Njeri", "Wanjiku", new DateOnly(1988, 3, 4), "female", "+254700111222", "njeri.wanjiku@example.test", ContactChannel.Sms, nairobiCentral.Id);
        var p2 = MakePatient("Kiprop", "Cheruiyot", new DateOnly(1975, 11, 20), "male", "+254700333444", null, ContactChannel.Sms, nairobiCentral.Id);
        var p3 = MakePatient("Zawadi", "Mwangi", new DateOnly(2016, 6, 10), "female", "+254700555666", "guardian.zm@example.test", ContactChannel.Sms, nairobiCentral.Id);
        var p4 = MakePatient("Harriet", "Wynn", new DateOnly(1961, 1, 30), "female", "+441700123456", "harriet.wynn@example.test", ContactChannel.Email, lonBranch.Id, vip: true);
        p1.GrantConsent(ConsentKind.ReminderCommunication, clock.UtcNow);
        p2.GrantConsent(ConsentKind.ReminderCommunication, clock.UtcNow);
        p4.GrantConsent(ConsentKind.ReminderCommunication, clock.UtcNow);
        db.Patients.AddRange(p1, p2, p3, p4);

        await db.SaveChangesAsync(ct);

        // ---- A couple of pre-existing appointments in the near future ----
        var tz = TimeZoneInfo.FindSystemTimeZoneById(nairobiCentral.TimeZoneId);
        var tomorrowLocal = clock.UtcNow.ToOffset(tz.GetUtcOffset(clock.UtcNow)).Date.AddDays(1);
        while (tomorrowLocal.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            tomorrowLocal = tomorrowLocal.AddDays(1);
        var localSlot = new DateTime(tomorrowLocal.Year, tomorrowLocal.Month, tomorrowLocal.Day, 10, 0, 0, DateTimeKind.Unspecified);
        var startUtc1 = new DateTimeOffset(localSlot, tz.GetUtcOffset(localSlot)).ToUniversalTime();
        var appt1 = Appointment.Book(p1.Id, drA.Id, nairobiCentral.Id, roomA.Id, atGp15.Id,
            startUtc1, startUtc1.AddMinutes(atGp15.DurationMinutes));
        var appt2 = Appointment.Book(p2.Id, drA.Id, nairobiCentral.Id, roomA.Id, atGp15.Id,
            startUtc1.AddMinutes(25), startUtc1.AddMinutes(40));
        db.Appointments.AddRange(appt1, appt2);
        await db.SaveChangesAsync(ct);
    }
}
