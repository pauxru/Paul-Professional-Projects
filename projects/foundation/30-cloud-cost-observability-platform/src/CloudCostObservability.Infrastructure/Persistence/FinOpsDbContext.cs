using CloudCostObservability.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Globalization;

namespace CloudCostObservability.Infrastructure.Persistence;

public sealed class FinOpsDbContext(DbContextOptions<FinOpsDbContext> options) : DbContext(options)
{
    public DbSet<CloudResource> Resources => Set<CloudResource>();
    public DbSet<CostRecord> Costs => Set<CostRecord>();
    public DbSet<FxRate> FxRates => Set<FxRate>();
    public DbSet<BudgetDefinition> Budgets => Set<BudgetDefinition>();
    public DbSet<AnomalyEvent> Anomalies => Set<AnomalyEvent>();
    public DbSet<Recommendation> Recommendations => Set<Recommendation>();
    public DbSet<AllocationRuleRow> AllocationRules => Set<AllocationRuleRow>();
    public DbSet<BusinessMetricRow> BusinessMetrics => Set<BusinessMetricRow>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new CloudResourceConfiguration());
        modelBuilder.ApplyConfiguration(new CostRecordConfiguration());
        modelBuilder.ApplyConfiguration(new FxRateConfiguration());
        modelBuilder.ApplyConfiguration(new BudgetConfiguration());
        modelBuilder.ApplyConfiguration(new AnomalyConfiguration());
        modelBuilder.ApplyConfiguration(new RecommendationConfiguration());
        modelBuilder.ApplyConfiguration(new AllocationRuleConfiguration());
        modelBuilder.ApplyConfiguration(new BusinessMetricConfiguration());
        modelBuilder.ApplyConfiguration(new ImportRunConfiguration());
        modelBuilder.ApplyConfiguration(new AuditRecordConfiguration());
    }
}

internal sealed class CloudResourceConfiguration : IEntityTypeConfiguration<CloudResource>
{
    public void Configure(EntityTypeBuilder<CloudResource> builder)
    {
        builder.ToTable("resources");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasMaxLength(220);
        builder.Property(x => x.Name).HasMaxLength(160);
        builder.Property(x => x.ResourceType).HasMaxLength(120);
        builder.Property(x => x.SubscriptionId).HasMaxLength(80);
        builder.Property(x => x.ResourceGroup).HasMaxLength(120);
        builder.Property(x => x.TagsJson).HasColumnType("TEXT");
        builder.HasIndex(x => new { x.SubscriptionId, x.ResourceGroup });
        builder.HasIndex(x => x.ParentResourceId);
        builder.HasIndex(x => x.Category);
    }
}

internal sealed class CostRecordConfiguration : IEntityTypeConfiguration<CostRecord>
{
    public void Configure(EntityTypeBuilder<CostRecord> builder)
    {
        builder.ToTable("cost_records");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasMaxLength(300);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(300);
        builder.Property(x => x.Meter).HasMaxLength(180);
        builder.Property(x => x.Service).HasMaxLength(120);
        builder.Property(x => x.Currency).HasMaxLength(3);
        builder.Property(x => x.UsageQuantity).HasConversion(SqliteDecimalConverter.Instance);
        builder.Property(x => x.Rate).HasConversion(SqliteDecimalConverter.Instance);
        builder.Property(x => x.ActualCost).HasConversion(SqliteDecimalConverter.Instance);
        builder.Property(x => x.AmortizedCost).HasConversion(SqliteDecimalConverter.Instance);
        builder.Property(x => x.Credits).HasConversion(SqliteDecimalConverter.Instance);
        builder.Property(x => x.Discounts).HasConversion(SqliteDecimalConverter.Instance);
        builder.Property(x => x.ReservedCoveragePercent).HasConversion(SqliteDecimalConverter.Instance);
        builder.Property(x => x.ReservedUtilizationPercent).HasConversion(SqliteDecimalConverter.Instance);
        builder.HasIndex(x => new { x.BillingPeriod, x.ResourceId, x.Meter, x.UsageDate, x.Hour }).IsUnique();
        builder.HasIndex(x => new { x.UsageDate, x.Service });
        builder.HasIndex(x => new { x.ResourceId, x.UsageDate });
        builder.HasIndex(x => x.BillingPeriod);
    }
}

internal sealed class FxRateConfiguration : IEntityTypeConfiguration<FxRate>
{
    public void Configure(EntityTypeBuilder<FxRate> builder)
    {
        builder.ToTable("fx_rates");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Rate).HasConversion(SqliteDecimalConverter.Instance);
        builder.HasIndex(x => new { x.RateDate, x.FromCurrency, x.ToCurrency }).IsUnique();
    }
}

internal sealed class BudgetConfiguration : IEntityTypeConfiguration<BudgetDefinition>
{
    public void Configure(EntityTypeBuilder<BudgetDefinition> builder)
    {
        builder.ToTable("budgets");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Amount).HasConversion(SqliteDecimalConverter.Instance);
        builder.Property(x => x.AlertStateJson).HasColumnType("TEXT");
        builder.HasIndex(x => new { x.Scope, x.Selector, x.PeriodStart, x.PeriodEnd });
    }
}

internal sealed class AnomalyConfiguration : IEntityTypeConfiguration<AnomalyEvent>
{
    public void Configure(EntityTypeBuilder<AnomalyEvent> builder)
    {
        builder.ToTable("anomalies");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Observed).HasConversion(SqliteDecimalConverter.Instance);
        builder.Property(x => x.Expected).HasConversion(SqliteDecimalConverter.Instance);
        builder.Property(x => x.SeverityScore).HasConversion(SqliteDecimalConverter.Instance);
        builder.HasIndex(x => new { x.DetectedOn, x.Grain, x.Dimension });
        builder.HasIndex(x => new { x.Suppressed, x.Acknowledged });
    }
}

internal sealed class RecommendationConfiguration : IEntityTypeConfiguration<Recommendation>
{
    public void Configure(EntityTypeBuilder<Recommendation> builder)
    {
        builder.ToTable("recommendations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProjectedMonthlySavings).HasConversion(SqliteDecimalConverter.Instance);
        builder.Property(x => x.BaselineMonthlyCost).HasConversion(SqliteNullableDecimalConverter.Instance);
        builder.Property(x => x.RealisedMonthlySavings).HasConversion(SqliteNullableDecimalConverter.Instance);
        builder.HasIndex(x => new { x.Lifecycle, x.Type });
        builder.HasIndex(x => x.ResourceId);
    }
}

public sealed class AllocationRuleRow
{
    public int Id { get; set; }
    public int Order { get; set; }
    public AllocationMethod Method { get; set; }
    public string? MatchKey { get; set; }
    public string? MatchValue { get; set; }
    public string? TargetTeam { get; set; }
    public string? TargetCostCentre { get; set; }
    public SharedSplitStrategy? SplitStrategy { get; set; }
    public string FixedPercentagesJson { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
}

internal sealed class AllocationRuleConfiguration : IEntityTypeConfiguration<AllocationRuleRow>
{
    public void Configure(EntityTypeBuilder<AllocationRuleRow> builder)
    {
        builder.ToTable("allocation_rules");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.Order).IsUnique();
        builder.Property(x => x.FixedPercentagesJson).HasColumnType("TEXT");
    }
}

public sealed class BusinessMetricRow
{
    public string Id { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public string Team { get; set; } = string.Empty;
    public int Orders { get; set; }
    public int ActiveTenants { get; set; }
    public decimal GigabytesProcessed { get; set; }
    public BusinessMetric ToDomain() => new(Date, Team, Orders, ActiveTenants, GigabytesProcessed);
    public static BusinessMetricRow From(BusinessMetric metric) => new()
    {
        Id = $"{metric.Date:yyyyMMdd}|{metric.Team.ToLowerInvariant()}",
        Date = metric.Date,
        Team = metric.Team,
        Orders = metric.Orders,
        ActiveTenants = metric.ActiveTenants,
        GigabytesProcessed = metric.GigabytesProcessed
    };
}

internal sealed class BusinessMetricConfiguration : IEntityTypeConfiguration<BusinessMetricRow>
{
    public void Configure(EntityTypeBuilder<BusinessMetricRow> builder)
    {
        builder.ToTable("business_metrics");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.GigabytesProcessed).HasConversion(SqliteDecimalConverter.Instance);
        builder.HasIndex(x => new { x.Date, x.Team }).IsUnique();
    }
}

public sealed class ImportRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ImportProvider Provider { get; set; }
    public int Inserted { get; set; }
    public int Restated { get; set; }
    public int Rejected { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}

internal sealed class ImportRunConfiguration : IEntityTypeConfiguration<ImportRun>
{
    public void Configure(EntityTypeBuilder<ImportRun> builder)
    {
        builder.ToTable("import_runs");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.CompletedAt);
    }
}

public sealed class AuditRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Actor { get; set; } = "system";
    public string Action { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string SourceIp { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string BeforeAfterHash { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> builder)
    {
        builder.ToTable("audit_records");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.Resource, x.OccurredAt });
        builder.Property(x => x.BeforeAfterHash).HasMaxLength(128);
    }
}

internal sealed class SqliteDecimalConverter : ValueConverter<decimal, string>
{
    public static readonly SqliteDecimalConverter Instance = new();

    private SqliteDecimalConverter()
        : base(
            value => value.ToString(CultureInfo.InvariantCulture),
            value => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture))
    {
    }
}

internal sealed class SqliteNullableDecimalConverter : ValueConverter<decimal?, string?>
{
    public static readonly SqliteNullableDecimalConverter Instance = new();

    private SqliteNullableDecimalConverter()
        : base(
            value => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : null,
            value => string.IsNullOrWhiteSpace(value) ? null : decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture))
    {
    }
}
