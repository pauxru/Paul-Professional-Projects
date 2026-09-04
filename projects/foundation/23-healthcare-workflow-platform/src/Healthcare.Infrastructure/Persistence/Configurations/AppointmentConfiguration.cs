using Healthcare.Domain.Appointments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Healthcare.Infrastructure.Persistence.Configurations;

public sealed class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
    public void Configure(EntityTypeBuilder<Appointment> b)
    {
        b.ToTable("appointments");
        b.HasKey(a => a.Id);
        b.Property(a => a.CancellationNotes).HasMaxLength(1024);
        b.Property(a => a.RowVersion).IsConcurrencyToken().IsRequired();
        b.HasIndex(a => new { a.ClinicianId, a.StartUtc });
        b.HasIndex(a => new { a.RoomId, a.StartUtc });
        b.HasIndex(a => new { a.PatientId, a.StartUtc });
        b.HasIndex(a => a.Status);
        // Slot-locking unique constraint: exactly one active appointment per (clinician, start).
        // Filtered on Status != Cancelled|NoShow so that a cancelled slot can be re-booked.
        b.HasIndex(a => new { a.ClinicianId, a.StartUtc })
            .IsUnique()
            .HasFilter($"\"Status\" NOT IN ({(int)AppointmentStatus.Cancelled}, {(int)AppointmentStatus.NoShow})")
            .HasDatabaseName("ux_appointments_clinician_slot_active");
        b.HasIndex(a => new { a.RoomId, a.StartUtc })
            .IsUnique()
            .HasFilter($"\"Status\" NOT IN ({(int)AppointmentStatus.Cancelled}, {(int)AppointmentStatus.NoShow})")
            .HasDatabaseName("ux_appointments_room_slot_active");
        b.Ignore(a => a.DomainEvents);
    }
}

public sealed class AppointmentTypeConfiguration : IEntityTypeConfiguration<AppointmentType>
{
    public void Configure(EntityTypeBuilder<AppointmentType> b)
    {
        b.ToTable("appointment_types");
        b.HasKey(a => a.Id);
        b.Property(a => a.Code).HasMaxLength(32).IsRequired();
        b.Property(a => a.Name).HasMaxLength(128).IsRequired();
        b.Property(a => a.RequiredRoomCapability).HasMaxLength(64);
        b.HasIndex(a => a.Code).IsUnique();
        b.Ignore(a => a.DomainEvents);
    }
}

public sealed class RecurringSeriesConfiguration : IEntityTypeConfiguration<RecurringSeries>
{
    public void Configure(EntityTypeBuilder<RecurringSeries> b)
    {
        b.ToTable("recurring_series");
        b.HasKey(r => r.Id);
        b.Property(r => r.ExceptionDatesCsv).HasMaxLength(4096);
        b.Ignore(r => r.DomainEvents);
    }
}
