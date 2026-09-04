using Microsoft.EntityFrameworkCore;

namespace Northstar.Reliability.Infrastructure.Persistence;

public sealed class ReliabilityDbContext(DbContextOptions<ReliabilityDbContext> options) : DbContext(options)
{
    public DbSet<ServiceEntity> Services => Set<ServiceEntity>();
    public DbSet<SliEntity> Slis => Set<SliEntity>();
    public DbSet<SloEntity> Slos => Set<SloEntity>();
    public DbSet<MetricEntity> Metrics => Set<MetricEntity>();
    public DbSet<AlertEntity> Alerts => Set<AlertEntity>();
    public DbSet<MaintenanceWindowEntity> MaintenanceWindows => Set<MaintenanceWindowEntity>();
    public DbSet<IncidentEntity> Incidents => Set<IncidentEntity>();
    public DbSet<PostmortemEntity> Postmortems => Set<PostmortemEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ServiceEntityConfiguration());
        modelBuilder.ApplyConfiguration(new SliEntityConfiguration());
        modelBuilder.ApplyConfiguration(new SloEntityConfiguration());
        modelBuilder.ApplyConfiguration(new MetricEntityConfiguration());
        modelBuilder.ApplyConfiguration(new AlertEntityConfiguration());
        modelBuilder.ApplyConfiguration(new MaintenanceWindowEntityConfiguration());
        modelBuilder.ApplyConfiguration(new IncidentEntityConfiguration());
        modelBuilder.ApplyConfiguration(new PostmortemEntityConfiguration());
    }
}

public sealed class ServiceEntity
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Tier { get; set; }
    public string OwningTeam { get; set; } = string.Empty;
    public string OnCallRotation { get; set; } = string.Empty;
    public string RepositoryUrl { get; set; } = string.Empty;
    public string? RunbookUrl { get; set; }
    public string DependenciesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public int Version { get; set; }
}

public sealed class SliEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ServiceSlug { get; set; } = string.Empty;
    public int AggregationMode { get; set; }
    public int Kind { get; set; }
    public string FilterJson { get; set; } = "{}";
    public decimal? LatencyThresholdMilliseconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SloEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid SliId { get; set; }
    public string ServiceSlug { get; set; } = string.Empty;
    public decimal Target { get; set; }
    public int WindowKind { get; set; }
    public int RollingDays { get; set; }
    public int CalendarPeriod { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MetricEntity
{
    public Guid Id { get; set; }
    public string ServiceSlug { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    public long Requests { get; set; }
    public long Errors { get; set; }
    public long LatencyGoodRequests { get; set; }
    public long QualityGoodEvents { get; set; }
    public long QualityValidEvents { get; set; }
    public long FreshnessGoodEvents { get; set; }
    public long FreshnessValidEvents { get; set; }
    public long ProbeGoodMinutes { get; set; }
    public long ProbeTotalMinutes { get; set; }
    public double P50LatencyMilliseconds { get; set; }
    public double P95LatencyMilliseconds { get; set; }
    public string LatencyHistogramJson { get; set; } = "[]";
    public int Resolution { get; set; }
}

public sealed class AlertEntity
{
    public Guid Id { get; set; }
    public Guid SloId { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MaintenanceWindowEntity
{
    public Guid Id { get; set; }
    public string ServiceSlug { get; set; } = string.Empty;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string PayloadJson { get; set; } = "{}";
}

public sealed class IncidentEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string PayloadJson { get; set; } = "{}";
}

public sealed class PostmortemEntity
{
    public Guid Id { get; set; }
    public Guid IncidentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string PayloadJson { get; set; } = "{}";
}
