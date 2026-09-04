using FieldOps.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldOps.Infrastructure;

internal static class ConfigurationHelpers
{
    public static void TenantIndex<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : class, ITenantOwned =>
        builder.HasIndex(x => x.TenantId);
}

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Slug).HasMaxLength(80);
        builder.Property(x => x.Name).HasMaxLength(200);
        builder.Property(x => x.Region).HasMaxLength(24);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.Plan).HasConversion<string>().HasMaxLength(24);
        builder.HasIndex(x => x.Slug).IsUnique();
    }
}

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Email).HasMaxLength(254);
        builder.Property(x => x.DisplayName).HasMaxLength(160);
        builder.HasIndex(x => x.Email).IsUnique();
    }
}

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Role).HasConversion<string>().HasMaxLength(24);
        builder.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(160);
        builder.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class TeamMembershipConfiguration : IEntityTypeConfiguration<TeamMembership>
{
    public void Configure(EntityTypeBuilder<TeamMembership> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.TenantId, x.TeamId, x.UserId }).IsUnique();
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Email).HasMaxLength(254);
        builder.Property(x => x.TokenHash).HasMaxLength(64);
        builder.Property(x => x.Role).HasConversion<string>().HasMaxLength(24);
        builder.HasIndex(x => new { x.TenantId, x.TokenHash }).IsUnique();
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.AssetTag).HasMaxLength(80);
        builder.Property(x => x.Name).HasMaxLength(200);
        builder.Property(x => x.Category).HasMaxLength(100);
        builder.Property(x => x.Location).HasMaxLength(200);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
        builder.HasIndex(x => new { x.TenantId, x.AssetTag }).IsUnique();
        builder.HasIndex(x => new { x.TenantId, x.Status });
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(240);
        builder.Property(x => x.Priority).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
        builder.HasIndex(x => new { x.TenantId, x.Status, x.ScheduleStart });
        builder.HasIndex(x => new { x.TenantId, x.AssigneeUserId });
        builder.HasIndex(x => new { x.TenantId, x.SlaDueAt });
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class JobAttachmentConfiguration : IEntityTypeConfiguration<JobAttachment>
{
    public void Configure(EntityTypeBuilder<JobAttachment> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ObjectKey).HasMaxLength(500);
        builder.Property(x => x.FileName).HasMaxLength(255);
        builder.Property(x => x.ContentType).HasMaxLength(120);
        builder.HasIndex(x => new { x.TenantId, x.JobId });
        builder.HasIndex(x => x.ObjectKey).IsUnique();
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class InspectionTemplateConfiguration : IEntityTypeConfiguration<InspectionTemplate>
{
    public void Configure(EntityTypeBuilder<InspectionTemplate> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200);
        builder.Property(x => x.PassingScore).HasPrecision(5, 2);
        builder.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(x => new { x.TenantId, x.Name });
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class InspectionTemplateItemConfiguration : IEntityTypeConfiguration<InspectionTemplateItem>
{
    public void Configure(EntityTypeBuilder<InspectionTemplateItem> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Prompt).HasMaxLength(500);
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.Weight).HasPrecision(8, 2);
        builder.Property(x => x.MinValue).HasPrecision(18, 4);
        builder.Property(x => x.MaxValue).HasPrecision(18, 4);
        builder.HasIndex(x => new { x.TenantId, x.TemplateId });
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class InspectionSubmissionConfiguration : IEntityTypeConfiguration<InspectionSubmission>
{
    public void Configure(EntityTypeBuilder<InspectionSubmission> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Score).HasPrecision(5, 2);
        builder.HasMany(x => x.Answers).WithOne().HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Answers).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(x => new { x.TenantId, x.JobId, x.SubmittedAt });
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class InspectionAnswerConfiguration : IEntityTypeConfiguration<InspectionAnswer>
{
    public void Configure(EntityTypeBuilder<InspectionAnswer> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Value).HasMaxLength(2_000);
        builder.HasIndex(x => new { x.TenantId, x.SubmissionId, x.ItemId }).IsUnique();
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class UsageCounterConfiguration : IEntityTypeConfiguration<UsageCounter>
{
    public void Configure(EntityTypeBuilder<UsageCounter> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Metric).HasMaxLength(80);
        builder.HasIndex(x => new { x.TenantId, x.Metric, x.PeriodStart }).IsUnique();
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class FeatureFlagConfiguration : IEntityTypeConfiguration<FeatureFlag>
{
    public void Configure(EntityTypeBuilder<FeatureFlag> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Key).HasMaxLength(120);
        builder.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class FeatureFlagOverrideConfiguration : IEntityTypeConfiguration<FeatureFlagOverride>
{
    public void Configure(EntityTypeBuilder<FeatureFlagOverride> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FlagKey).HasMaxLength(120);
        builder.HasIndex(x => new { x.TenantId, x.FlagKey, x.UserId }).IsUnique();
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class BillingCustomerConfiguration : IEntityTypeConfiguration<BillingCustomer>
{
    public void Configure(EntityTypeBuilder<BillingCustomer> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProviderCustomerId).HasMaxLength(120);
        builder.HasIndex(x => new { x.TenantId, x.ProviderCustomerId }).IsUnique();
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class BillingSubscriptionConfiguration : IEntityTypeConfiguration<BillingSubscription>
{
    public void Configure(EntityTypeBuilder<BillingSubscription> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProviderSubscriptionId).HasMaxLength(120);
        builder.Property(x => x.Plan).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.Currency).HasMaxLength(3);
        builder.HasIndex(x => new { x.TenantId, x.ProviderSubscriptionId }).IsUnique();
        ConfigurationHelpers.TenantIndex(builder);
    }
}

internal sealed class WebhookReceiptConfiguration : IEntityTypeConfiguration<WebhookReceipt>
{
    public void Configure(EntityTypeBuilder<WebhookReceipt> builder)
    {
        builder.HasKey(x => x.EventId);
        builder.Property(x => x.EventId).HasMaxLength(160);
        builder.Property(x => x.EventType).HasMaxLength(80);
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ActorId).HasMaxLength(160);
        builder.Property(x => x.Action).HasMaxLength(160);
        builder.Property(x => x.Resource).HasMaxLength(500);
        builder.Property(x => x.BeforeHash).HasMaxLength(64);
        builder.Property(x => x.AfterHash).HasMaxLength(64);
        builder.Property(x => x.CorrelationId).HasMaxLength(160);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasMaxLength(500);
        builder.HasIndex(x => new { x.TenantId, x.OccurredAt });
        builder.HasIndex(x => new { x.TenantId, x.Action, x.OccurredAt });
        ConfigurationHelpers.TenantIndex(builder);
    }
}
