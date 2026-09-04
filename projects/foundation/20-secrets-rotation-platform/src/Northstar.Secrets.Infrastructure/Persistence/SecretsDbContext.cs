using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Northstar.Secrets.Domain;

namespace Northstar.Secrets.Infrastructure.Persistence;

public sealed class SecretsDbContext(DbContextOptions<SecretsDbContext> options) : DbContext(options)
{
    public DbSet<SecretRecord> Secrets => Set<SecretRecord>();
    public DbSet<SecretVersion> SecretVersions => Set<SecretVersion>();
    public DbSet<SecretTag> SecretTags => Set<SecretTag>();
    public DbSet<Consumer> Consumers => Set<Consumer>();
    public DbSet<SecretConsumer> SecretConsumers => Set<SecretConsumer>();
    public DbSet<RotationOperation> Rotations => Set<RotationOperation>();
    public DbSet<ConsumerAcknowledgement> ConsumerAcknowledgements => Set<ConsumerAcknowledgement>();
    public DbSet<AccessPolicy> AccessPolicies => Set<AccessPolicy>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SecretsDbContext).Assembly);
}

internal sealed class SecretRecordConfiguration : IEntityTypeConfiguration<SecretRecord>
{
    public void Configure(EntityTypeBuilder<SecretRecord> builder)
    {
        builder.ToTable("Secrets");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(160).IsRequired();
        builder.HasIndex(x => x.Name).IsUnique();
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
        builder.Property(x => x.OwnerTeam).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Environment).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Criticality).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Description).HasMaxLength(1000);
        builder.Property(x => x.ConcurrencyVersion);
        builder.Ignore(x => x.Reference);
        builder.Ignore(x => x.CurrentVersion);
        builder.HasMany(x => x.Versions)
            .WithOne()
            .HasForeignKey(x => x.SecretId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Tags)
            .WithOne()
            .HasForeignKey(x => x.SecretId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Consumers)
            .WithOne(x => x.Secret)
            .HasForeignKey(x => x.SecretId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SecretVersionConfiguration : IEntityTypeConfiguration<SecretVersion>
{
    public void Configure(EntityTypeBuilder<SecretVersion> builder)
    {
        builder.ToTable("SecretVersions");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.SecretId, x.VersionNumber }).IsUnique();
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Ciphertext).IsRequired();
        builder.Property(x => x.Nonce).IsRequired();
        builder.Property(x => x.AuthenticationTag).IsRequired();
        builder.Property(x => x.WrappedDataEncryptionKey).IsRequired();
        builder.Property(x => x.KeyVersion).HasMaxLength(80).IsRequired();
    }
}

internal sealed class SecretTagConfiguration : IEntityTypeConfiguration<SecretTag>
{
    public void Configure(EntityTypeBuilder<SecretTag> builder)
    {
        builder.ToTable("SecretTags");
        builder.HasKey(x => new { x.SecretId, x.Value });
        builder.Property(x => x.Value).HasMaxLength(80);
    }
}

internal sealed class ConsumerConfiguration : IEntityTypeConfiguration<Consumer>
{
    public void Configure(EntityTypeBuilder<Consumer> builder)
    {
        builder.ToTable("Consumers");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Application).HasMaxLength(120).IsRequired();
        builder.Property(x => x.WebhookUrl).HasMaxLength(500);
        builder.Property(x => x.Email).HasMaxLength(200);
        builder.HasIndex(x => new { x.Application, x.Name }).IsUnique();
        builder.HasMany(x => x.SecretLinks)
            .WithOne(x => x.Consumer)
            .HasForeignKey(x => x.ConsumerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SecretConsumerConfiguration : IEntityTypeConfiguration<SecretConsumer>
{
    public void Configure(EntityTypeBuilder<SecretConsumer> builder)
    {
        builder.ToTable("SecretConsumers");
        builder.HasKey(x => new { x.SecretId, x.ConsumerId });
    }
}

internal sealed class RotationOperationConfiguration : IEntityTypeConfiguration<RotationOperation>
{
    public void Configure(EntityTypeBuilder<RotationOperation> builder)
    {
        builder.ToTable("Rotations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Strategy).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(40);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
        builder.HasIndex(x => x.IdempotencyKey).IsUnique();
        builder.HasIndex(x => new { x.SecretId, x.State });
        builder.Property(x => x.RequestedBy).HasMaxLength(160);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);
        builder.Property(x => x.FailureReason).HasMaxLength(1000);
        builder.Ignore(x => x.IsTerminal);
        builder.HasMany(x => x.Acknowledgements)
            .WithOne()
            .HasForeignKey(x => x.RotationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ConsumerAcknowledgementConfiguration
    : IEntityTypeConfiguration<ConsumerAcknowledgement>
{
    public void Configure(EntityTypeBuilder<ConsumerAcknowledgement> builder)
    {
        builder.ToTable("ConsumerAcknowledgements");
        builder.HasKey(x => new { x.RotationId, x.ConsumerId });
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
    }
}

internal sealed class AccessPolicyConfiguration : IEntityTypeConfiguration<AccessPolicy>
{
    public void Configure(EntityTypeBuilder<AccessPolicy> builder)
    {
        builder.ToTable("AccessPolicies");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Subject).HasMaxLength(160).IsRequired();
        builder.Property(x => x.PathPattern).HasMaxLength(160).IsRequired();
        builder.HasIndex(x => new { x.Subject, x.PathPattern }).IsUnique();
    }
}

internal sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> builder)
    {
        builder.ToTable("AuditRecords");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Actor).HasMaxLength(160).IsRequired();
        builder.Property(x => x.Action).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Resource).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Outcome).HasMaxLength(30).IsRequired();
        builder.Property(x => x.SourceAddress).HasMaxLength(100);
        builder.Property(x => x.UserAgent).HasMaxLength(500);
        builder.HasIndex(x => new { x.SecretId, x.OccurredAt });
        builder.HasIndex(x => new { x.Actor, x.OccurredAt });
    }
}

internal sealed class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> builder)
    {
        builder.ToTable("ApprovalRequests");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Operation).HasConversion<string>().HasMaxLength(40);
        builder.Property(x => x.Resource).HasMaxLength(200).IsRequired();
        builder.Property(x => x.RequestedBy).HasMaxLength(160).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.ApprovedBy).HasMaxLength(160);
    }
}
