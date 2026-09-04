using Microsoft.EntityFrameworkCore;
using Healthcare.Domain.Facilities;
using Healthcare.Domain.Clinicians;
using Healthcare.Domain.Patients;
using Healthcare.Domain.Appointments;
using Healthcare.Domain.Encounters;
using Healthcare.Domain.Referrals;
using Healthcare.Domain.Waitlist;
using Healthcare.Domain.Reminders;
using Healthcare.Domain.Audit;

namespace Healthcare.Application.Abstractions;

/// <summary>
/// Application-side abstraction over EF Core so the Application layer never directly references
/// Infrastructure. The Infrastructure DbContext implements this contract.
/// </summary>
public interface IAppDbContext
{
    DbSet<Facility> Facilities { get; }
    DbSet<Room> Rooms { get; }
    DbSet<OperatingHours> OperatingHours { get; }
    DbSet<Closure> Closures { get; }
    DbSet<Clinician> Clinicians { get; }
    DbSet<ClinicianFacility> ClinicianFacilities { get; }
    DbSet<WorkingPattern> WorkingPatterns { get; }
    DbSet<LeavePeriod> LeavePeriods { get; }
    DbSet<Patient> Patients { get; }
    DbSet<Consent> Consents { get; }
    DbSet<Appointment> Appointments { get; }
    DbSet<AppointmentType> AppointmentTypes { get; }
    DbSet<RecurringSeries> RecurringSeries { get; }
    DbSet<Encounter> Encounters { get; }
    DbSet<ClinicalNote> ClinicalNotes { get; }
    DbSet<VitalReading> VitalReadings { get; }
    DbSet<Referral> Referrals { get; }
    DbSet<WaitlistEntry> WaitlistEntries { get; }
    DbSet<Reminder> Reminders { get; }
    DbSet<AuditEvent> AuditEvents { get; }

    Task<int> SaveChangesAsync(CancellationToken ct);
}
