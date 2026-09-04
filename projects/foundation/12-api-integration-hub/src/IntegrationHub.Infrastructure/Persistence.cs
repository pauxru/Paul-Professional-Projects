using IntegrationHub.Application;
using IntegrationHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IntegrationHub.Infrastructure;

public sealed class IntegrationHubDbContext(DbContextOptions<IntegrationHubDbContext> options) : DbContext(options)
{
    public DbSet<FlowEntity> Flows => Set<FlowEntity>();
    public DbSet<FlowVersionEntity> FlowVersions => Set<FlowVersionEntity>();
    public DbSet<RunEntity> Runs => Set<RunEntity>();
    public DbSet<StepEntity> Steps => Set<StepEntity>();
    public DbSet<DeadLetterEntity> DeadLetters => Set<DeadLetterEntity>();
    public DbSet<CheckpointEntity> Checkpoints => Set<CheckpointEntity>();
    public DbSet<IdempotencyEntity> IdempotencyKeys => Set<IdempotencyEntity>();
    public DbSet<DriftAlertEntity> DriftAlerts => Set<DriftAlertEntity>();
    public DbSet<WebhookNonceEntity> WebhookNonces => Set<WebhookNonceEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IntegrationHubDbContext).Assembly);
    }
}

public sealed class FlowEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? ActiveVersion { get; set; }
    public List<FlowVersionEntity> Versions { get; set; } = [];
}

public sealed class FlowVersionEntity
{
    public Guid FlowId { get; set; }
    public int Version { get; set; }
    public string Format { get; set; } = string.Empty;
    public string Definition { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public FlowEntity Flow { get; set; } = null!;
}

public sealed class RunEntity
{
    public Guid Id { get; set; }
    public Guid FlowId { get; set; }
    public int FlowVersion { get; set; }
    public RunStatus Status { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int RecordsProcessed { get; set; }
    public string? Error { get; set; }
    public List<StepEntity> Steps { get; set; } = [];
}

public sealed class StepEntity
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string StepId { get; set; } = string.Empty;
    public FlowStepKind Kind { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string? InputSnapshot { get; set; }
    public string? OutputSnapshot { get; set; }
    public int RecordsIn { get; set; }
    public int RecordsOut { get; set; }
    public string? Error { get; set; }
    public RunEntity Run { get; set; } = null!;
}

public sealed class DeadLetterEntity
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string StepId { get; set; } = string.Empty;
    public string BatchKey { get; set; } = string.Empty;
    public string RecordKey { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public DeadLetterStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ReplayRunId { get; set; }
}

public sealed class CheckpointEntity
{
    public Guid RunId { get; set; }
    public string StepId { get; set; } = string.Empty;
    public string BatchKey { get; set; } = string.Empty;
    public int NextIndex { get; set; }
}

public sealed class IdempotencyEntity
{
    public string Scope { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DriftAlertEntity
{
    public Guid Id { get; set; }
    public string ConnectorId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string FieldPath { get; set; } = string.Empty;
    public string Change { get; set; } = string.Empty;
    public DateTimeOffset DetectedAt { get; set; }
    public bool Resolved { get; set; }
}

public sealed class WebhookNonceEntity
{
    public string Nonce { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class FlowConfiguration : IEntityTypeConfiguration<FlowEntity>
{
    public void Configure(EntityTypeBuilder<FlowEntity> builder)
    {
        builder.ToTable("Flows");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(160).IsRequired();
        builder.HasIndex(x => x.Name);
        builder.HasMany(x => x.Versions).WithOne(x => x.Flow).HasForeignKey(x => x.FlowId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class FlowVersionConfiguration : IEntityTypeConfiguration<FlowVersionEntity>
{
    public void Configure(EntityTypeBuilder<FlowVersionEntity> builder)
    {
        builder.ToTable("FlowVersions");
        builder.HasKey(x => new { x.FlowId, x.Version });
        builder.Property(x => x.Format).HasMaxLength(8).IsRequired();
        builder.Property(x => x.Definition).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(160).IsRequired();
    }
}

public sealed class RunConfiguration : IEntityTypeConfiguration<RunEntity>
{
    public void Configure(EntityTypeBuilder<RunEntity> builder)
    {
        builder.ToTable("Runs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.HasIndex(x => new { x.FlowId, x.StartedAt });
        builder.HasIndex(x => new { x.Status, x.StartedAt });
        builder.HasIndex(x => x.CorrelationId);
        builder.HasMany(x => x.Steps).WithOne(x => x.Run).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class StepConfiguration : IEntityTypeConfiguration<StepEntity>
{
    public void Configure(EntityTypeBuilder<StepEntity> builder)
    {
        builder.ToTable("RunSteps");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.StepId).HasMaxLength(100).IsRequired();
        builder.HasIndex(x => new { x.RunId, x.StartedAt });
    }
}

public sealed class DeadLetterConfiguration : IEntityTypeConfiguration<DeadLetterEntity>
{
    public void Configure(EntityTypeBuilder<DeadLetterEntity> builder)
    {
        builder.ToTable("DeadLetters");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.StepId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.BatchKey).HasMaxLength(200).IsRequired();
        builder.Property(x => x.RecordKey).HasMaxLength(300).IsRequired();
        builder.HasIndex(x => new { x.RunId, x.BatchKey });
        builder.HasIndex(x => new { x.Status, x.CreatedAt });
        builder.HasIndex(x => new { x.RunId, x.StepId, x.RecordKey }).IsUnique();
    }
}

public sealed class CheckpointConfiguration : IEntityTypeConfiguration<CheckpointEntity>
{
    public void Configure(EntityTypeBuilder<CheckpointEntity> builder)
    {
        builder.ToTable("Checkpoints");
        builder.HasKey(x => new { x.RunId, x.StepId, x.BatchKey });
    }
}

public sealed class IdempotencyConfiguration : IEntityTypeConfiguration<IdempotencyEntity>
{
    public void Configure(EntityTypeBuilder<IdempotencyEntity> builder)
    {
        builder.ToTable("IdempotencyKeys");
        builder.HasKey(x => new { x.Scope, x.Key });
        builder.Property(x => x.Scope).HasMaxLength(160);
        builder.Property(x => x.Key).HasMaxLength(300);
        builder.HasIndex(x => x.CreatedAt);
    }
}

public sealed class DriftAlertConfiguration : IEntityTypeConfiguration<DriftAlertEntity>
{
    public void Configure(EntityTypeBuilder<DriftAlertEntity> builder)
    {
        builder.ToTable("ContractDriftAlerts");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.Resolved, x.DetectedAt });
        builder.HasIndex(x => new { x.ConnectorId, x.Operation, x.FieldPath });
    }
}

public sealed class WebhookNonceConfiguration : IEntityTypeConfiguration<WebhookNonceEntity>
{
    public void Configure(EntityTypeBuilder<WebhookNonceEntity> builder)
    {
        builder.ToTable("WebhookNonces");
        builder.HasKey(x => x.Nonce);
        builder.Property(x => x.Nonce).HasMaxLength(200);
        builder.HasIndex(x => x.ExpiresAt);
    }
}
