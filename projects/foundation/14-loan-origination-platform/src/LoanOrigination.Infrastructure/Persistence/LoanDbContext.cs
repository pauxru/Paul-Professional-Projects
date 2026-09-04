using Microsoft.EntityFrameworkCore;

namespace LoanOrigination.Infrastructure.Persistence;

public sealed class LoanDbContext(DbContextOptions<LoanDbContext> options) : DbContext(options)
{
    public DbSet<CustomerRow> Customers => Set<CustomerRow>();
    public DbSet<ProductRow> Products => Set<ProductRow>();
    public DbSet<RulesetRow> Rulesets => Set<RulesetRow>();
    public DbSet<ApplicationRow> Applications => Set<ApplicationRow>();
    public DbSet<QueueRow> UnderwritingQueue => Set<QueueRow>();
    public DbSet<OfferRow> Offers => Set<OfferRow>();
    public DbSet<DisbursementRow> Disbursements => Set<DisbursementRow>();
    public DbSet<AuditRow> Audits => Set<AuditRow>();
    public DbSet<DecisionRecordRow> DecisionRecords => Set<DecisionRecordRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CustomerRow>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Payload).IsRequired();
            entity.HasIndex(row => row.LegalName);
            entity.HasIndex(row => row.DeduplicationKey).IsUnique();
        });
        modelBuilder.Entity<ProductRow>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Payload).IsRequired();
            entity.HasIndex(row => new { row.ProductCode, row.Version }).IsUnique();
        });
        modelBuilder.Entity<RulesetRow>(entity =>
        {
            entity.HasKey(row => new { row.RulesetId, row.Version });
            entity.Property(row => row.Payload).IsRequired();
        });
        modelBuilder.Entity<ApplicationRow>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Payload).IsRequired();
            entity.HasIndex(row => row.CustomerId);
            entity.HasIndex(row => new { row.Stage, row.CreatedAtUnixMilliseconds });
        });
        modelBuilder.Entity<QueueRow>(entity =>
        {
            entity.HasKey(row => row.ApplicationId);
            entity.Property(row => row.Payload).IsRequired();
            entity.HasIndex(row => new { row.Priority, row.SlaDueAtUnixMilliseconds });
        });
        modelBuilder.Entity<OfferRow>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Payload).IsRequired();
            entity.HasIndex(row => new { row.ApplicationId, row.Version }).IsUnique();
        });
        modelBuilder.Entity<DisbursementRow>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Payload).IsRequired();
            entity.HasIndex(row => row.ProviderReference).IsUnique();
        });
        modelBuilder.Entity<AuditRow>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Payload).IsRequired();
            entity.HasIndex(row => row.OccurredAtUnixMilliseconds);
            entity.HasIndex(row => row.CorrelationId);
        });
        modelBuilder.Entity<DecisionRecordRow>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Payload).IsRequired();
            entity.HasIndex(row => row.ApplicationId).IsUnique();
        });
    }
}

public sealed class CustomerRow
{
    public Guid Id { get; set; }
    public string LegalName { get; set; } = string.Empty;
    public string DeduplicationKey { get; set; } = string.Empty;
    public long CreatedAtUnixMilliseconds { get; set; }
    public string Payload { get; set; } = string.Empty;
}

public sealed class ProductRow
{
    public Guid Id { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public int Version { get; set; }
    public long EffectiveFromUnixMilliseconds { get; set; }
    public string Payload { get; set; } = string.Empty;
}

public sealed class RulesetRow
{
    public string RulesetId { get; set; } = string.Empty;
    public int Version { get; set; }
    public long EffectiveFromUnixMilliseconds { get; set; }
    public string Payload { get; set; } = string.Empty;
}

public sealed class ApplicationRow
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string Stage { get; set; } = string.Empty;
    public int Version { get; set; }
    public long CreatedAtUnixMilliseconds { get; set; }
    public string Payload { get; set; } = string.Empty;
}

public sealed class QueueRow
{
    public Guid ApplicationId { get; set; }
    public int Priority { get; set; }
    public long SlaDueAtUnixMilliseconds { get; set; }
    public string Payload { get; set; } = string.Empty;
}

public sealed class OfferRow
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public int Version { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
}

public sealed class DisbursementRow
{
    public Guid Id { get; set; }
    public string ProviderReference { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
}

public sealed class AuditRow
{
    public Guid Id { get; set; }
    public long OccurredAtUnixMilliseconds { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
}

public sealed class DecisionRecordRow
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public long CreatedAtUnixMilliseconds { get; set; }
    public string Payload { get; set; } = string.Empty;
}
