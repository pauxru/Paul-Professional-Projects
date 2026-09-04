using Microsoft.EntityFrameworkCore;
using Healthcare.Application.Abstractions;
using Healthcare.Domain.Facilities;
using Healthcare.Domain.Clinicians;
using Healthcare.Domain.Patients;
using Healthcare.Domain.Appointments;
using Healthcare.Domain.Encounters;
using Healthcare.Domain.Referrals;
using Healthcare.Domain.Waitlist;
using Healthcare.Domain.Reminders;
using Healthcare.Domain.Audit;
using Healthcare.Infrastructure.Persistence.Configurations;

namespace Healthcare.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext, IAppDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Facility> Facilities => Set<Facility>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<OperatingHours> OperatingHours => Set<OperatingHours>();
    public DbSet<Closure> Closures => Set<Closure>();
    public DbSet<Clinician> Clinicians => Set<Clinician>();
    public DbSet<ClinicianFacility> ClinicianFacilities => Set<ClinicianFacility>();
    public DbSet<WorkingPattern> WorkingPatterns => Set<WorkingPattern>();
    public DbSet<LeavePeriod> LeavePeriods => Set<LeavePeriod>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Consent> Consents => Set<Consent>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentType> AppointmentTypes => Set<AppointmentType>();
    public DbSet<RecurringSeries> RecurringSeries => Set<RecurringSeries>();
    public DbSet<Encounter> Encounters => Set<Encounter>();
    public DbSet<ClinicalNote> ClinicalNotes => Set<ClinicalNote>();
    public DbSet<VitalReading> VitalReadings => Set<VitalReading>();
    public DbSet<Referral> Referrals => Set<Referral>();
    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new FacilityConfiguration());
        modelBuilder.ApplyConfiguration(new RoomConfiguration());
        modelBuilder.ApplyConfiguration(new OperatingHoursConfiguration());
        modelBuilder.ApplyConfiguration(new ClosureConfiguration());
        modelBuilder.ApplyConfiguration(new ClinicianConfiguration());
        modelBuilder.ApplyConfiguration(new ClinicianFacilityConfiguration());
        modelBuilder.ApplyConfiguration(new WorkingPatternConfiguration());
        modelBuilder.ApplyConfiguration(new LeavePeriodConfiguration());
        modelBuilder.ApplyConfiguration(new PatientConfiguration());
        modelBuilder.ApplyConfiguration(new ConsentConfiguration());
        modelBuilder.ApplyConfiguration(new AppointmentConfiguration());
        modelBuilder.ApplyConfiguration(new AppointmentTypeConfiguration());
        modelBuilder.ApplyConfiguration(new RecurringSeriesConfiguration());
        modelBuilder.ApplyConfiguration(new EncounterConfiguration());
        modelBuilder.ApplyConfiguration(new ClinicalNoteConfiguration());
        modelBuilder.ApplyConfiguration(new VitalReadingConfiguration());
        modelBuilder.ApplyConfiguration(new ReferralConfiguration());
        modelBuilder.ApplyConfiguration(new WaitlistEntryConfiguration());
        modelBuilder.ApplyConfiguration(new ReminderConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEventConfiguration());

        // SQLite does not natively support ORDER BY / comparison operators on DateTimeOffset. We
        // store all DateTimeOffset values as UTC Ticks (long, INTEGER in SQLite) which preserves
        // ordering and round-trips exactly when values are UTC-based (as they are throughout this
        // codebase). We deliberately drop the offset — the domain only stores UTC instants.
        var dtoConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
            v => v.ToUniversalTime().Ticks,
            s => new DateTimeOffset(s, TimeSpan.Zero));
        var dtoNullConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?>(
            v => v == null ? null : v.Value.ToUniversalTime().Ticks,
            s => s == null ? null : new DateTimeOffset(s.Value, TimeSpan.Zero));
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                    property.SetValueConverter(dtoConverter);
                else if (property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(dtoNullConverter);
            }
        }
    }

    public override int SaveChanges()
    {
        RejectImmutableWrites();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        RejectImmutableWrites();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Enforce append-only for ClinicalNote and AuditEvent at the persistence layer. This is defence
    /// in depth on top of domain modelling: modifications or deletions are refused before EF issues
    /// any SQL. An entity is only flagged as truly Modified when at least one property has actually
    /// changed value; that avoids false-positives from EF's ChangeTracker occasionally marking
    /// unrelated loads as Modified (e.g. due to non-trivial value converters).
    /// </summary>
    private void RejectImmutableWrites()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is not (ClinicalNote or AuditEvent)) continue;
            if (entry.State == EntityState.Deleted)
                throw new InvalidOperationException(
                    $"Entity {entry.Entity.GetType().Name} is append-only; DELETE is forbidden.");
            if (entry.State == EntityState.Modified)
            {
                var changed = entry.Properties.Where(p => p.IsModified &&
                    !object.Equals(p.OriginalValue, p.CurrentValue)).ToList();
                if (changed.Count == 0)
                {
                    // Defensive: mark as Unchanged to prevent EF from issuing an UPDATE.
                    entry.State = EntityState.Unchanged;
                    continue;
                }
                throw new InvalidOperationException(
                    $"Entity {entry.Entity.GetType().Name} is append-only; UPDATE is forbidden. Changed: {string.Join(",", changed.Select(p => p.Metadata.Name))}");
            }
        }
    }
}
