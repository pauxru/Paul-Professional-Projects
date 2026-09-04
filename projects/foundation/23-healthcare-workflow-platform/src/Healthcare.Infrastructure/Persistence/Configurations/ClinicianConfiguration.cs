using Healthcare.Domain.Clinicians;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Healthcare.Infrastructure.Persistence.Configurations;

public sealed class ClinicianConfiguration : IEntityTypeConfiguration<Clinician>
{
    public void Configure(EntityTypeBuilder<Clinician> b)
    {
        b.ToTable("clinicians");
        b.HasKey(c => c.Id);
        b.Property(c => c.GivenName).HasMaxLength(128).IsRequired();
        b.Property(c => c.FamilyName).HasMaxLength(128).IsRequired();
        b.Property(c => c.Speciality).HasMaxLength(128).IsRequired();
        b.Property(c => c.QualificationsCsv).HasMaxLength(512);
        b.HasMany(c => c.Sites).WithOne().HasForeignKey(s => s.ClinicianId);
        b.HasMany(c => c.WorkingPatterns).WithOne().HasForeignKey(p => p.ClinicianId);
        b.HasMany(c => c.Leaves).WithOne().HasForeignKey(l => l.ClinicianId);
        b.Ignore(c => c.DomainEvents);
    }
}

public sealed class ClinicianFacilityConfiguration : IEntityTypeConfiguration<ClinicianFacility>
{
    public void Configure(EntityTypeBuilder<ClinicianFacility> b)
    {
        b.ToTable("clinician_facilities");
        b.HasKey(cf => cf.Id);
        b.HasIndex(cf => new { cf.ClinicianId, cf.FacilityId }).IsUnique();
    }
}

public sealed class WorkingPatternConfiguration : IEntityTypeConfiguration<WorkingPattern>
{
    public void Configure(EntityTypeBuilder<WorkingPattern> b)
    {
        b.ToTable("working_patterns");
        b.HasKey(w => w.Id);
        b.HasIndex(w => new { w.ClinicianId, w.FacilityId, w.Day }).IsUnique();
    }
}

public sealed class LeavePeriodConfiguration : IEntityTypeConfiguration<LeavePeriod>
{
    public void Configure(EntityTypeBuilder<LeavePeriod> b)
    {
        b.ToTable("leave_periods");
        b.HasKey(l => l.Id);
        b.Property(l => l.Reason).HasMaxLength(256);
        b.HasIndex(l => new { l.ClinicianId, l.StartDate });
    }
}
