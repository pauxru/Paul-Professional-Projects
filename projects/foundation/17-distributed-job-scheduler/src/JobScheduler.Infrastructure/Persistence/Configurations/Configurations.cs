using JobScheduler.Domain;
using JobScheduler.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JobScheduler.Infrastructure.Persistence.Configurations;

public sealed class JobDefinitionConfiguration : IEntityTypeConfiguration<JobDefinition>
{
    public void Configure(EntityTypeBuilder<JobDefinition> b)
    {
        b.ToTable("JobDefinitions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.HandlerType).HasMaxLength(200).IsRequired();
        b.Property(x => x.PayloadJson).IsRequired();
        b.Property(x => x.Queue).HasMaxLength(100).IsRequired();
        b.Property(x => x.Owner).HasMaxLength(200);
        b.Property(x => x.TagsCsv).HasMaxLength(1000);
        b.Property(x => x.DependsOnCsv).HasMaxLength(2000);
        b.Property(x => x.TimeZoneId).HasMaxLength(100);
        b.Property(x => x.CronExpression).HasMaxLength(200);
        b.Property(x => x.RetryStrategy).HasConversion<string>().HasMaxLength(40);
        b.Property(x => x.TriggerType).HasConversion<string>().HasMaxLength(40);
        b.Property(x => x.MisfirePolicy).HasConversion<string>().HasMaxLength(40);
        b.HasIndex(x => new { x.Enabled, x.TriggerType });
    }
}

public sealed class JobRunConfiguration : IEntityTypeConfiguration<JobRun>
{
    public void Configure(EntityTypeBuilder<JobRun> b)
    {
        b.ToTable("JobRuns");
        b.HasKey(x => x.Id);
        b.Property(x => x.State).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(x => x.JobName).HasMaxLength(200);
        b.Property(x => x.HandlerType).HasMaxLength(200);
        b.Property(x => x.Queue).HasMaxLength(100);
        b.Property(x => x.IdempotencyKey).HasMaxLength(300).IsRequired();
        b.Property(x => x.CorrelationId).HasMaxLength(100);
        b.Property(x => x.TriggerKind).HasMaxLength(60);
        b.Property(x => x.LeaseOwner).HasMaxLength(100);

        // The idempotency key is the dedupe guard for schedule materialisation and DLQ replay.
        b.HasIndex(x => x.IdempotencyKey).IsUnique();

        // The hot path: workers scan Pending runs whose scheduled time has arrived, best first.
        b.HasIndex(x => new { x.State, x.ScheduledAt });
        // Per-definition concurrency counting and singleton checks.
        b.HasIndex(x => new { x.JobDefinitionId, x.State });
        // Per-queue slot counting.
        b.HasIndex(x => new { x.Queue, x.State });
        // Reaper scan for expired leases.
        b.HasIndex(x => new { x.State, x.LeaseExpiresAt });
        // Workflow fan-in lookups.
        b.HasIndex(x => new { x.CorrelationId, x.JobName, x.State });
    }
}

public sealed class WorkerNodeConfiguration : IEntityTypeConfiguration<WorkerNode>
{
    public void Configure(EntityTypeBuilder<WorkerNode> b)
    {
        b.ToTable("WorkerNodes");
        b.HasKey(x => x.NodeId);
        b.Property(x => x.NodeId).HasMaxLength(100);
        b.Property(x => x.Hostname).HasMaxLength(200);
        b.Property(x => x.TagsCsv).HasMaxLength(1000);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => x.LastHeartbeat);
    }
}

public sealed class LeaderLeaseConfiguration : IEntityTypeConfiguration<LeaderLease>
{
    public void Configure(EntityTypeBuilder<LeaderLease> b)
    {
        b.ToTable("LeaderLeases");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(100);
        b.Property(x => x.Owner).HasMaxLength(100);
    }
}

public sealed class DeadLetterConfiguration : IEntityTypeConfiguration<DeadLetterEntry>
{
    public void Configure(EntityTypeBuilder<DeadLetterEntry> b)
    {
        b.ToTable("DeadLetters");
        b.HasKey(x => x.Id);
        b.Property(x => x.JobName).HasMaxLength(200);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasIndex(x => new { x.Replayed, x.DeadLetteredAt });
        b.HasIndex(x => x.JobRunId);
    }
}

public sealed class RunLogConfiguration : IEntityTypeConfiguration<RunLog>
{
    public void Configure(EntityTypeBuilder<RunLog> b)
    {
        b.ToTable("RunLogs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Level).HasMaxLength(20);
        b.Property(x => x.Message).HasMaxLength(4000);
        b.Property(x => x.CorrelationId).HasMaxLength(100);
        b.Property(x => x.NodeId).HasMaxLength(100);
        b.HasIndex(x => new { x.JobRunId, x.Timestamp });
        b.HasIndex(x => x.Timestamp);
    }
}
