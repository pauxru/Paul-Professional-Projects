using Healthcare.Domain.Facilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Healthcare.Infrastructure.Persistence.Configurations;

public sealed class FacilityConfiguration : IEntityTypeConfiguration<Facility>
{
    public void Configure(EntityTypeBuilder<Facility> b)
    {
        b.ToTable("facilities");
        b.HasKey(f => f.Id);
        b.Property(f => f.Code).HasMaxLength(32).IsRequired();
        b.HasIndex(f => f.Code).IsUnique();
        b.Property(f => f.Name).HasMaxLength(256).IsRequired();
        b.Property(f => f.TimeZoneId).HasMaxLength(64).IsRequired();
        b.Ignore(f => f.DomainEvents);
        b.HasMany(f => f.Rooms).WithOne().HasForeignKey(r => r.FacilityId);
        b.HasMany(f => f.Hours).WithOne().HasForeignKey(h => h.FacilityId);
        b.HasMany(f => f.Closures).WithOne().HasForeignKey(c => c.FacilityId);
    }
}

public sealed class RoomConfiguration : IEntityTypeConfiguration<Room>
{
    public void Configure(EntityTypeBuilder<Room> b)
    {
        b.ToTable("rooms");
        b.HasKey(r => r.Id);
        b.Property(r => r.Name).HasMaxLength(128).IsRequired();
        b.Property(r => r.CapabilitiesCsv).HasMaxLength(512);
        b.HasIndex(r => new { r.FacilityId, r.Name }).IsUnique();
    }
}

public sealed class OperatingHoursConfiguration : IEntityTypeConfiguration<OperatingHours>
{
    public void Configure(EntityTypeBuilder<OperatingHours> b)
    {
        b.ToTable("operating_hours");
        b.HasKey(h => h.Id);
        b.HasIndex(h => new { h.FacilityId, h.Day }).IsUnique();
    }
}

public sealed class ClosureConfiguration : IEntityTypeConfiguration<Closure>
{
    public void Configure(EntityTypeBuilder<Closure> b)
    {
        b.ToTable("closures");
        b.HasKey(c => c.Id);
        b.Property(c => c.Reason).HasMaxLength(256);
        b.HasIndex(c => new { c.FacilityId, c.Date }).IsUnique();
    }
}
