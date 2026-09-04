using System.Net;
using Healthcare.Application.Abstractions;
using Healthcare.Domain.Appointments;
using Healthcare.Domain.Clinicians;
using Healthcare.Domain.Common;
using Healthcare.Domain.Facilities;
using Healthcare.Domain.Patients;
using Healthcare.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Healthcare.IntegrationTests;

/// <summary>
/// Shared helper to seed a small deterministic dataset into the in-memory SQLite database.
/// Test fixtures call this per class so tests don't share state.
/// </summary>
public static class TestSeed
{
    public sealed record Seeded(Guid FacilityId, Guid RoomId, Guid ClinicianId, Guid ClinicianOtherId,
        Guid PatientId, Guid Patient2Id, Guid GpTypeId, Guid ProcTypeId);

    public static async Task<Seeded> SeedAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        // Idempotent: return the existing seeded state if it's already there.
        var existingFacility = db.Facilities.FirstOrDefault(f => f.Code == "NBO-TEST");
        if (existingFacility is not null)
        {
            var rooms = db.Rooms.Where(r => r.FacilityId == existingFacility.Id).ToList();
            var clinicians = db.Clinicians.ToList();
            var apptTypes = db.AppointmentTypes.ToList();
            var patients = db.Patients.ToList();
            return new Seeded(
                existingFacility.Id,
                rooms.First().Id,
                clinicians.First(c => c.GivenName == "Alice").Id,
                clinicians.First(c => c.GivenName == "Bob").Id,
                patients.First(p => p.GivenName == "Njeri").Id,
                patients.First(p => p.GivenName == "Kiprop").Id,
                apptTypes.First(a => a.Code == "GP15").Id,
                apptTypes.First(a => a.Code == "PROC30").Id);
        }

        var facility = Facility.Create("NBO-TEST", "Nairobi Demo Clinic (fictional)", "Africa/Nairobi");
        for (var d = DayOfWeek.Monday; d <= DayOfWeek.Friday; d++)
            facility.SetHours(d, new TimeOnly(8, 0), new TimeOnly(18, 0));
        var roomA = facility.AddRoom("Room A", new[] { "gp" });
        db.Facilities.Add(facility);

        var clin = Clinician.Create("Alice", "Test", "General Practitioner", new[] { "MBChB (fictional)" });
        clin.AssignToFacility(facility.Id);
        for (var d = DayOfWeek.Monday; d <= DayOfWeek.Friday; d++)
            clin.SetWorkingPattern(facility.Id, d, new TimeOnly(9, 0), new TimeOnly(17, 0));

        var clinOther = Clinician.Create("Bob", "Other", "General Practitioner", new[] { "MBChB (fictional)" });
        clinOther.AssignToFacility(facility.Id);
        for (var d = DayOfWeek.Monday; d <= DayOfWeek.Friday; d++)
            clinOther.SetWorkingPattern(facility.Id, d, new TimeOnly(9, 0), new TimeOnly(17, 0));

        db.Clinicians.Add(clin);
        db.Clinicians.Add(clinOther);

        var gp = AppointmentType.Create("GP15", "GP Consultation", 15, 5, "gp");
        var proc = AppointmentType.Create("PROC30", "Procedure", 30, 10, "procedure");
        db.AppointmentTypes.Add(gp);
        db.AppointmentTypes.Add(proc);

        var p1 = Patient.Register(PatientId.Create(1), "Njeri", "Wanjiku", new DateOnly(1988, 1, 1), "female",
            "+254700111222", "n.w@example.test", ContactChannel.Sms, facility.Id);
        p1.GrantConsent(ConsentKind.ReminderCommunication, clock.UtcNow);
        var p2 = Patient.Register(PatientId.Create(2), "Kiprop", "Cheruiyot", new DateOnly(1975, 5, 5), "male",
            "+254700333444", null, ContactChannel.Sms, facility.Id);
        p2.GrantConsent(ConsentKind.ReminderCommunication, clock.UtcNow);
        db.Patients.Add(p1);
        db.Patients.Add(p2);

        await db.SaveChangesAsync();
        return new Seeded(facility.Id, roomA.Id, clin.Id, clinOther.Id, p1.Id, p2.Id, gp.Id, proc.Id);
    }
}
