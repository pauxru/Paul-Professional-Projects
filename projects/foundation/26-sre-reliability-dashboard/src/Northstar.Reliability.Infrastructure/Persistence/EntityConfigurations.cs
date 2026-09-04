using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Northstar.Reliability.Infrastructure.Persistence;

public sealed class ServiceEntityConfiguration : IEntityTypeConfiguration<ServiceEntity>
{
    public void Configure(EntityTypeBuilder<ServiceEntity> builder)
    {
        builder.ToTable("services");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Slug).HasMaxLength(80).IsRequired();
        builder.Property(entity => entity.Name).HasMaxLength(160).IsRequired();
        builder.Property(entity => entity.OwningTeam).HasMaxLength(120).IsRequired();
        builder.Property(entity => entity.DependenciesJson).IsRequired();
        builder.HasIndex(entity => entity.Slug).IsUnique();
        builder.Property(entity => entity.Version).IsConcurrencyToken();
    }
}

public sealed class SliEntityConfiguration : IEntityTypeConfiguration<SliEntity>
{
    public void Configure(EntityTypeBuilder<SliEntity> builder)
    {
        builder.ToTable("slis");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Name).HasMaxLength(160).IsRequired();
        builder.Property(entity => entity.ServiceSlug).HasMaxLength(80).IsRequired();
        builder.Property(entity => entity.FilterJson).IsRequired();
        builder.HasIndex(entity => new { entity.ServiceSlug, entity.Name }).IsUnique();
    }
}

public sealed class SloEntityConfiguration : IEntityTypeConfiguration<SloEntity>
{
    public void Configure(EntityTypeBuilder<SloEntity> builder)
    {
        builder.ToTable("slos");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Name).HasMaxLength(160).IsRequired();
        builder.Property(entity => entity.ServiceSlug).HasMaxLength(80).IsRequired();
        builder.HasIndex(entity => entity.SliId);
        builder.HasIndex(entity => new { entity.ServiceSlug, entity.Name }).IsUnique();
    }
}

public sealed class MetricEntityConfiguration : IEntityTypeConfiguration<MetricEntity>
{
    public void Configure(EntityTypeBuilder<MetricEntity> builder)
    {
        builder.ToTable("metrics");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.ServiceSlug).HasMaxLength(80).IsRequired();
        builder.Property(entity => entity.Endpoint).HasMaxLength(240).IsRequired();
        builder.Property(entity => entity.Region).HasMaxLength(80).IsRequired();
        builder.Property(entity => entity.Tier).HasMaxLength(40).IsRequired();
        builder.Property(entity => entity.LatencyHistogramJson).IsRequired();
        builder.HasIndex(entity => new { entity.ServiceSlug, entity.Timestamp });
    }
}

public sealed class AlertEntityConfiguration : IEntityTypeConfiguration<AlertEntity>
{
    public void Configure(EntityTypeBuilder<AlertEntity> builder)
    {
        builder.ToTable("alerts");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.RuleName).HasMaxLength(80).IsRequired();
        builder.Property(entity => entity.PayloadJson).IsRequired();
        builder.HasIndex(entity => new { entity.SloId, entity.RuleName }).IsUnique();
    }
}

public sealed class MaintenanceWindowEntityConfiguration : IEntityTypeConfiguration<MaintenanceWindowEntity>
{
    public void Configure(EntityTypeBuilder<MaintenanceWindowEntity> builder)
    {
        builder.ToTable("maintenance_windows");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.ServiceSlug).HasMaxLength(80).IsRequired();
        builder.Property(entity => entity.PayloadJson).IsRequired();
        builder.HasIndex(entity => new { entity.ServiceSlug, entity.StartsAt, entity.EndsAt });
    }
}

public sealed class IncidentEntityConfiguration : IEntityTypeConfiguration<IncidentEntity>
{
    public void Configure(EntityTypeBuilder<IncidentEntity> builder)
    {
        builder.ToTable("incidents");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.PayloadJson).IsRequired();
        builder.HasIndex(entity => entity.StartedAt);
    }
}

public sealed class PostmortemEntityConfiguration : IEntityTypeConfiguration<PostmortemEntity>
{
    public void Configure(EntityTypeBuilder<PostmortemEntity> builder)
    {
        builder.ToTable("postmortems");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.PayloadJson).IsRequired();
        builder.HasIndex(entity => entity.IncidentId);
    }
}
