using FieldOps.Application;
using FieldOps.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FieldOps.Infrastructure;

public sealed class FieldOpsDbContext(
    DbContextOptions<FieldOpsDbContext> options,
    ITenantContext tenantContext) : DbContext(options)
{
    private Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMembership> TeamMemberships => Set<TeamMembership>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobAttachment> JobAttachments => Set<JobAttachment>();
    public DbSet<InspectionTemplate> InspectionTemplates => Set<InspectionTemplate>();
    public DbSet<InspectionTemplateItem> InspectionTemplateItems => Set<InspectionTemplateItem>();
    public DbSet<InspectionSubmission> InspectionSubmissions => Set<InspectionSubmission>();
    public DbSet<InspectionAnswer> InspectionAnswers => Set<InspectionAnswer>();
    public DbSet<UsageCounter> UsageCounters => Set<UsageCounter>();
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();
    public DbSet<FeatureFlagOverride> FeatureFlagOverrides => Set<FeatureFlagOverride>();
    public DbSet<BillingCustomer> BillingCustomers => Set<BillingCustomer>();
    public DbSet<BillingSubscription> BillingSubscriptions => Set<BillingSubscription>();
    public DbSet<WebhookReceipt> WebhookReceipts => Set<WebhookReceipt>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FieldOpsDbContext).Assembly);

        modelBuilder.Entity<Membership>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<Team>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<TeamMembership>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<Invitation>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<Asset>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<Job>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<JobAttachment>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<InspectionTemplate>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<InspectionTemplateItem>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<InspectionSubmission>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<InspectionAnswer>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<UsageCounter>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<FeatureFlag>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<FeatureFlagOverride>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<BillingCustomer>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<BillingSubscription>().HasQueryFilter(x => x.TenantId == CurrentTenantId);
        modelBuilder.Entity<AuditEntry>().HasQueryFilter(x => x.TenantId == CurrentTenantId);

        var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, long>(
            value => value.ToUnixTimeMilliseconds(),
            value => DateTimeOffset.FromUnixTimeMilliseconds(value));
        var nullableDateTimeOffsetConverter = new ValueConverter<DateTimeOffset?, long?>(
            value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null,
            value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(x => x.GetProperties()))
        {
            if (property.ClrType == typeof(DateTimeOffset))
            {
                property.SetValueConverter(dateTimeOffsetConverter);
            }
            else if (property.ClrType == typeof(DateTimeOffset?))
            {
                property.SetValueConverter(nullableDateTimeOffsetConverter);
            }
        }
    }
}
