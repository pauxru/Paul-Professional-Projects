using Healthcare.Domain.Patients;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Healthcare.Infrastructure.Persistence.Configurations;

public sealed class PatientConfiguration : IEntityTypeConfiguration<Patient>
{
    public void Configure(EntityTypeBuilder<Patient> b)
    {
        b.ToTable("patients");
        b.HasKey(p => p.Id);
        b.Property(p => p.GivenName).HasMaxLength(128).IsRequired();
        b.Property(p => p.FamilyName).HasMaxLength(128).IsRequired();
        b.Property(p => p.Sex).HasMaxLength(32);
        b.Property(p => p.PhoneE164).HasMaxLength(32).IsRequired();
        b.Property(p => p.Email).HasMaxLength(256);
        b.Property(p => p.AllergiesFreeText).HasMaxLength(2048);
        b.Property(p => p.ExternalId)
            .HasColumnName("external_id")
            .HasMaxLength(32)
            .IsRequired()
            .HasConversion(v => v.Value, s => PatientId.Parse(s));
        b.HasIndex(p => p.ExternalId).IsUnique();
        b.HasMany(p => p.Consents).WithOne().HasForeignKey("PatientId");
        b.Ignore(p => p.DomainEvents);
    }
}

public sealed class ConsentConfiguration : IEntityTypeConfiguration<Consent>
{
    public void Configure(EntityTypeBuilder<Consent> b)
    {
        b.ToTable("consents");
        b.HasKey(c => c.Id);
        b.Property<Guid>("PatientId");
        b.HasIndex("PatientId", "Kind").IsUnique();
    }
}
