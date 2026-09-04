using FeatureFlags.Application;
using Microsoft.EntityFrameworkCore;

namespace FeatureFlags.Infrastructure.Persistence;

public sealed class FeatureFlagDbContext(DbContextOptions<FeatureFlagDbContext> options) : DbContext(options)
{
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<EnvironmentEntity> Environments => Set<EnvironmentEntity>();
    public DbSet<AuditEntryEntity> AuditEntries => Set<AuditEntryEntity>();
    public DbSet<ApprovalRequestEntity> ApprovalRequests => Set<ApprovalRequestEntity>();
    public DbSet<EvaluationMetricEntity> EvaluationMetrics => Set<EvaluationMetricEntity>();
    public DbSet<AnalyticsEventEntity> AnalyticsEvents => Set<AnalyticsEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectEntity>(builder =>
        {
            builder.ToTable("projects");
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Key).HasMaxLength(100).IsRequired();
            builder.Property(entity => entity.Name).HasMaxLength(200).IsRequired();
            builder.HasIndex(entity => entity.Key).IsUnique();
        });

        modelBuilder.Entity<EnvironmentEntity>(builder =>
        {
            builder.ToTable("environments");
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Key).HasMaxLength(100).IsRequired();
            builder.Property(entity => entity.Name).HasMaxLength(200).IsRequired();
            builder.Property(entity => entity.ServerSdkKey).HasMaxLength(300).IsRequired();
            builder.Property(entity => entity.ClientSdkKey).HasMaxLength(300).IsRequired();
            builder.Property(entity => entity.ConfigurationJson).IsRequired();
            builder.HasIndex(entity => new { entity.ProjectId, entity.Key }).IsUnique();
            builder.HasIndex(entity => entity.ServerSdkKey).IsUnique();
            builder.HasIndex(entity => entity.ClientSdkKey).IsUnique();
            builder.HasOne(entity => entity.Project).WithMany(project => project.Environments).HasForeignKey(entity => entity.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditEntryEntity>(builder =>
        {
            builder.ToTable("audit_entries");
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Actor).HasMaxLength(200).IsRequired();
            builder.Property(entity => entity.Action).HasMaxLength(100).IsRequired();
            builder.Property(entity => entity.Resource).HasMaxLength(300).IsRequired();
            builder.HasIndex(entity => new { entity.ProjectKey, entity.EnvironmentKey, entity.OccurredAt });
        });

        modelBuilder.Entity<ApprovalRequestEntity>(builder =>
        {
            builder.ToTable("approval_requests");
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Status).HasConversion<string>().HasMaxLength(30);
            builder.HasIndex(entity => new { entity.ProjectKey, entity.EnvironmentKey, entity.Status });
        });

        modelBuilder.Entity<EvaluationMetricEntity>(builder =>
        {
            builder.ToTable("evaluation_metrics");
            builder.HasKey(entity => entity.Id);
            builder.HasIndex(entity => new { entity.ProjectKey, entity.EnvironmentKey, entity.FlagKey, entity.VariationIndex }).IsUnique();
        });

        modelBuilder.Entity<AnalyticsEventEntity>(builder =>
        {
            builder.ToTable("analytics_events");
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Kind).HasMaxLength(100).IsRequired();
            builder.Property(entity => entity.ContextKey).HasMaxLength(300).IsRequired();
            builder.HasIndex(entity => new { entity.ProjectKey, entity.EnvironmentKey, entity.FlagKey, entity.VariationIndex, entity.Kind });
            builder.HasIndex(entity => entity.OccurredAt);
        });
    }
}

public sealed class ProjectEntity
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<EnvironmentEntity> Environments { get; } = new List<EnvironmentEntity>();
}

public sealed class EnvironmentEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public ProjectEntity Project { get; set; } = null!;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ServerSdkKey { get; set; } = string.Empty;
    public string ClientSdkKey { get; set; } = string.Empty;
    public long Version { get; set; }
    public string ConfigurationJson { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AuditEntryEntity
{
    public Guid Id { get; set; }
    public string ProjectKey { get; set; } = string.Empty;
    public string EnvironmentKey { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string BeforeJson { get; set; } = string.Empty;
    public string AfterJson { get; set; } = string.Empty;
    public string DiffJson { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public string? TicketReference { get; set; }
    public string? CorrelationId { get; set; }
    public string? SourceIp { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class ApprovalRequestEntity
{
    public Guid Id { get; set; }
    public string ProjectKey { get; set; } = string.Empty;
    public string EnvironmentKey { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string ProposedConfigurationJson { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public string? RequestComment { get; set; }
    public ApprovalStatus Status { get; set; }
    public string? ReviewedBy { get; set; }
    public string? ReviewComment { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
}

public sealed class EvaluationMetricEntity
{
    public long Id { get; set; }
    public string ProjectKey { get; set; } = string.Empty;
    public string EnvironmentKey { get; set; } = string.Empty;
    public string FlagKey { get; set; } = string.Empty;
    public int? VariationIndex { get; set; }
    public long Count { get; set; }
    public DateTimeOffset LastEvaluatedAt { get; set; }
    public long UniqueContextCount { get; set; }
}

public sealed class AnalyticsEventEntity
{
    public long Id { get; set; }
    public string ProjectKey { get; set; } = string.Empty;
    public string EnvironmentKey { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? FlagKey { get; set; }
    public int? VariationIndex { get; set; }
    public string ContextKey { get; set; } = string.Empty;
    public string? MetricKey { get; set; }
    public decimal? NumericValue { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
