using Healthcare.Domain.Encounters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Healthcare.Infrastructure.Persistence.Configurations;

public sealed class EncounterConfiguration : IEntityTypeConfiguration<Encounter>
{
    public void Configure(EntityTypeBuilder<Encounter> b)
    {
        b.ToTable("encounters");
        b.HasKey(e => e.Id);
        b.HasMany(e => e.Notes).WithOne().HasForeignKey(n => n.EncounterId);
        b.HasMany(e => e.Vitals).WithOne().HasForeignKey(v => v.EncounterId);
        b.HasIndex(e => new { e.PatientId, e.OpenedAtUtc });
        b.HasIndex(e => e.AppointmentId).IsUnique();
        b.Ignore(e => e.DomainEvents);
    }
}

public sealed class ClinicalNoteConfiguration : IEntityTypeConfiguration<ClinicalNote>
{
    public void Configure(EntityTypeBuilder<ClinicalNote> b)
    {
        b.ToTable("clinical_notes");
        b.HasKey(n => n.Id);
        b.Property(n => n.ChiefComplaint).HasMaxLength(1024).IsRequired();
        b.Property(n => n.Observations).HasMaxLength(4096);
        b.Property(n => n.Assessment).HasMaxLength(4096).IsRequired();
        b.Property(n => n.Plan).HasMaxLength(4096);
        b.Property(n => n.AmendmentReason).HasMaxLength(512);
        b.HasIndex(n => new { n.RootNoteId, n.Version }).IsUnique();
    }
}

public sealed class VitalReadingConfiguration : IEntityTypeConfiguration<VitalReading>
{
    public void Configure(EntityTypeBuilder<VitalReading> b)
    {
        b.ToTable("vital_readings");
        b.HasKey(v => v.Id);
        b.Property(v => v.Kind).HasMaxLength(64).IsRequired();
        b.Property(v => v.Unit).HasMaxLength(16).IsRequired();
        b.Property(v => v.Value).HasPrecision(10, 2);
        b.HasIndex(v => new { v.EncounterId, v.Kind });
    }
}
