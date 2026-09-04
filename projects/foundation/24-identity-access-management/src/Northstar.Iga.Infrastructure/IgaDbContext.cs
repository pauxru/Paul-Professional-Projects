using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Northstar.Iga.Domain;

namespace Northstar.Iga.Infrastructure;

public sealed class IgaDbContext(DbContextOptions<IgaDbContext> options) : DbContext(options)
{
    public DbSet<UserIdentity> Users => Set<UserIdentity>();
    public DbSet<TargetApplication> Applications => Set<TargetApplication>();
    public DbSet<Entitlement> Entitlements => Set<Entitlement>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleEntitlement> RoleEntitlements => Set<RoleEntitlement>();
    public DbSet<RoleInheritance> RoleInheritances => Set<RoleInheritance>();
    public DbSet<UserRoleGrant> UserRoleGrants => Set<UserRoleGrant>();
    public DbSet<UserEntitlementGrant> UserEntitlementGrants => Set<UserEntitlementGrant>();
    public DbSet<UserEntitlementExclusion> UserEntitlementExclusions => Set<UserEntitlementExclusion>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<GroupRole> GroupRoles => Set<GroupRole>();
    public DbSet<PolicyDefinition> Policies => Set<PolicyDefinition>();
    public DbSet<SoDRule> SoDRules => Set<SoDRule>();
    public DbSet<SoDException> SoDExceptions => Set<SoDException>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<ApprovalStep> ApprovalSteps => Set<ApprovalStep>();
    public DbSet<Elevation> Elevations => Set<Elevation>();
    public DbSet<CertificationCampaign> Campaigns => Set<CertificationCampaign>();
    public DbSet<CertificationItem> CertificationItems => Set<CertificationItem>();
    public DbSet<LifecycleWorkflow> LifecycleWorkflows => Set<LifecycleWorkflow>();
    public DbSet<LifecycleWorkflowStep> LifecycleWorkflowSteps => Set<LifecycleWorkflowStep>();
    public DbSet<ProvisioningAccount> ProvisioningAccounts => Set<ProvisioningAccount>();
    public DbSet<ProvisioningGrant> ProvisioningGrants => Set<ProvisioningGrant>();
    public DbSet<ProvisioningJob> ProvisioningJobs => Set<ProvisioningJob>();
    public DbSet<ProvisioningQuarantineItem> ProvisioningQuarantine => Set<ProvisioningQuarantineItem>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IgaDbContext).Assembly);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceAppendOnlyAudit();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnforceAppendOnlyAudit();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceAppendOnlyAudit()
    {
        if (ChangeTracker.Entries<AuditRecord>()
            .Any(x => x.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Audit records are append-only and cannot be updated or deleted.");
        }
    }
}

internal sealed class UserIdentityConfiguration : IEntityTypeConfiguration<UserIdentity>
{
    public void Configure(EntityTypeBuilder<UserIdentity> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.EmployeeNumber).HasMaxLength(32).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(254).IsRequired();
        builder.Property(x => x.Department).HasMaxLength(100).IsRequired();
        builder.Property(x => x.JobTitle).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Location).HasMaxLength(100).IsRequired();
        builder.Property(x => x.CostCentre).HasMaxLength(50).IsRequired();
        builder.Property(x => x.EmploymentType).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
        builder.HasIndex(x => x.EmployeeNumber).IsUnique();
        builder.HasIndex(x => x.Email).IsUnique();
        builder.HasIndex(x => new { x.Department, x.Status });
        builder.HasIndex(x => x.ManagerId);
    }
}

internal sealed class ApplicationConfiguration : IEntityTypeConfiguration<TargetApplication>
{
    public void Configure(EntityTypeBuilder<TargetApplication> builder)
    {
        builder.ToTable("Applications");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Key).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(160).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1000).IsRequired();
        builder.HasIndex(x => x.Key).IsUnique();
    }
}

internal sealed class EntitlementConfiguration : IEntityTypeConfiguration<Entitlement>
{
    public void Configure(EntityTypeBuilder<Entitlement> builder)
    {
        builder.ToTable("Entitlements");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Key).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Permission).HasMaxLength(240).IsRequired();
        builder.Property(x => x.BusinessDescription).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.Risk).HasConversion<string>().HasMaxLength(24);
        builder.HasOne<TargetApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.ApplicationId, x.Key }).IsUnique();
        builder.HasIndex(x => x.Permission).IsUnique();
        builder.HasIndex(x => new { x.Risk, x.IsPrivileged });
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Key).HasMaxLength(80).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(160).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.BirthrightRule).HasMaxLength(1000);
        builder.HasIndex(x => x.Key).IsUnique();
        builder.HasIndex(x => x.IsBirthright);
    }
}

internal sealed class RoleEntitlementConfiguration : IEntityTypeConfiguration<RoleEntitlement>
{
    public void Configure(EntityTypeBuilder<RoleEntitlement> builder)
    {
        builder.ToTable("RoleEntitlements");
        builder.HasKey(x => new { x.RoleId, x.EntitlementId });
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Entitlement>().WithMany().HasForeignKey(x => x.EntitlementId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.EntitlementId);
    }
}

internal sealed class RoleInheritanceConfiguration : IEntityTypeConfiguration<RoleInheritance>
{
    public void Configure(EntityTypeBuilder<RoleInheritance> builder)
    {
        builder.ToTable("RoleInheritances");
        builder.HasKey(x => new { x.RoleId, x.InheritedRoleId });
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.InheritedRoleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.InheritedRoleId);
    }
}

internal sealed class UserRoleGrantConfiguration : IEntityTypeConfiguration<UserRoleGrant>
{
    public void Configure(EntityTypeBuilder<UserRoleGrant> builder)
    {
        builder.ToTable("UserRoleGrants");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Source).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        builder.HasOne<UserIdentity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.UserId, x.RoleId, x.RevokedAt });
        builder.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class UserEntitlementGrantConfiguration : IEntityTypeConfiguration<UserEntitlementGrant>
{
    public void Configure(EntityTypeBuilder<UserEntitlementGrant> builder)
    {
        builder.ToTable("UserEntitlementGrants");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Source).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        builder.HasOne<UserIdentity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Entitlement>().WithMany().HasForeignKey(x => x.EntitlementId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.UserId, x.EntitlementId, x.RevokedAt });
        builder.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class UserEntitlementExclusionConfiguration : IEntityTypeConfiguration<UserEntitlementExclusion>
{
    public void Configure(EntityTypeBuilder<UserEntitlementExclusion> builder)
    {
        builder.ToTable("UserEntitlementExclusions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        builder.HasOne<UserIdentity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Entitlement>().WithMany().HasForeignKey(x => x.EntitlementId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.UserId, x.EntitlementId }).IsUnique();
    }
}

internal sealed class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    public void Configure(EntityTypeBuilder<Group> builder)
    {
        builder.ToTable("Groups");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Key).HasMaxLength(80).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(160).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.DynamicRule).HasMaxLength(1000);
        builder.HasIndex(x => x.Key).IsUnique();
        builder.HasIndex(x => x.Type);
    }
}

internal sealed class GroupMemberConfiguration : IEntityTypeConfiguration<GroupMember>
{
    public void Configure(EntityTypeBuilder<GroupMember> builder)
    {
        builder.ToTable("GroupMembers");
        builder.HasKey(x => new { x.GroupId, x.UserId });
        builder.Property(x => x.Source).HasConversion<string>().HasMaxLength(24);
        builder.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<UserIdentity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.UserId);
    }
}

internal sealed class GroupRoleConfiguration : IEntityTypeConfiguration<GroupRole>
{
    public void Configure(EntityTypeBuilder<GroupRole> builder)
    {
        builder.ToTable("GroupRoles");
        builder.HasKey(x => new { x.GroupId, x.RoleId });
        builder.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.RoleId);
    }
}

internal sealed class PolicyConfiguration : IEntityTypeConfiguration<PolicyDefinition>
{
    public void Configure(EntityTypeBuilder<PolicyDefinition> builder)
    {
        builder.ToTable("Policies");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(160).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.Effect).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.PermissionPattern).HasMaxLength(240).IsRequired();
        builder.Property(x => x.ConditionsJson).HasColumnType("TEXT").IsRequired();
        builder.HasIndex(x => new { x.Enabled, x.Priority });
    }
}

internal sealed class SoDRuleConfiguration : IEntityTypeConfiguration<SoDRule>
{
    public void Configure(EntityTypeBuilder<SoDRule> builder)
    {
        builder.ToTable("SoDRules");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(160).IsRequired();
        builder.Property(x => x.BusinessDescription).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.Severity).HasConversion<string>().HasMaxLength(24);
        builder.HasIndex(x => new { x.EntitlementAId, x.EntitlementBId }).IsUnique();
    }
}

internal sealed class SoDExceptionConfiguration : IEntityTypeConfiguration<SoDException>
{
    public void Configure(EntityTypeBuilder<SoDException> builder)
    {
        builder.ToTable("SoDExceptions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ApprovedBy).HasMaxLength(160).IsRequired();
        builder.Property(x => x.Justification).HasMaxLength(1000).IsRequired();
        builder.HasOne<SoDRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<UserIdentity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.UserId, x.RuleId, x.ExpiresAt });
    }
}

internal sealed class AccessRequestConfiguration : IEntityTypeConfiguration<AccessRequest>
{
    public void Configure(EntityTypeBuilder<AccessRequest> builder)
    {
        builder.ToTable("AccessRequests");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TargetType).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.Risk).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.Justification).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.RejectionReason).HasMaxLength(2000);
        builder.HasIndex(x => new { x.Status, x.RequestedAt });
        builder.HasIndex(x => x.UserId);
    }
}

internal sealed class ApprovalStepConfiguration : IEntityTypeConfiguration<ApprovalStep>
{
    public void Configure(EntityTypeBuilder<ApprovalStep> builder)
    {
        builder.ToTable("ApprovalSteps");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Kind).HasMaxLength(80).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.Reason).HasMaxLength(2000);
        builder.HasOne<AccessRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.RequestId, x.Stage });
        builder.HasIndex(x => new { x.ApproverId, x.Status, x.DueAt });
    }
}

internal sealed class ElevationConfiguration : IEntityTypeConfiguration<Elevation>
{
    public void Configure(EntityTypeBuilder<Elevation> builder)
    {
        builder.ToTable("Elevations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Justification).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.TicketReference).HasMaxLength(100).IsRequired();
        builder.Property(x => x.SessionRecordingReference).HasMaxLength(500);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
        builder.HasIndex(x => new { x.UserId, x.Status, x.EndsAt });
        builder.HasIndex(x => x.EntitlementId);
    }
}

internal sealed class CampaignConfiguration : IEntityTypeConfiguration<CertificationCampaign>
{
    public void Configure(EntityTypeBuilder<CertificationCampaign> builder)
    {
        builder.ToTable("CertificationCampaigns");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ScopeType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.ScopeValue).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ReviewerMode).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
        builder.HasIndex(x => new { x.Status, x.Deadline });
    }
}

internal sealed class CertificationItemConfiguration : IEntityTypeConfiguration<CertificationItem>
{
    public void Configure(EntityTypeBuilder<CertificationItem> builder)
    {
        builder.ToTable("CertificationItems");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Decision).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.DerivationJson).HasColumnType("TEXT").IsRequired();
        builder.Property(x => x.Justification).HasMaxLength(2000);
        builder.HasOne<CertificationCampaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.CampaignId, x.Decision });
        builder.HasIndex(x => new { x.ReviewerId, x.Decision });
        builder.HasIndex(x => new { x.CampaignId, x.UserId, x.EntitlementId }).IsUnique();
    }
}

internal sealed class LifecycleWorkflowConfiguration : IEntityTypeConfiguration<LifecycleWorkflow>
{
    public void Configure(EntityTypeBuilder<LifecycleWorkflow> builder)
    {
        builder.ToTable("LifecycleWorkflows");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Kind).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
        builder.HasIndex(x => new { x.UserId, x.CreatedAt });
    }
}

internal sealed class LifecycleWorkflowStepConfiguration : IEntityTypeConfiguration<LifecycleWorkflowStep>
{
    public void Configure(EntityTypeBuilder<LifecycleWorkflowStep> builder)
    {
        builder.ToTable("LifecycleWorkflowSteps");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.LastError).HasMaxLength(2000);
        builder.HasOne<LifecycleWorkflow>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.WorkflowId, x.Order }).IsUnique();
    }
}

internal sealed class ProvisioningAccountConfiguration : IEntityTypeConfiguration<ProvisioningAccount>
{
    public void Configure(EntityTypeBuilder<ProvisioningAccount> builder)
    {
        builder.ToTable("ProvisioningAccounts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ConnectorKey).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ExternalId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.UserName).HasMaxLength(254).IsRequired();
        builder.HasIndex(x => new { x.ConnectorKey, x.ExternalId }).IsUnique();
        builder.HasIndex(x => new { x.ConnectorKey, x.UserId });
        builder.HasIndex(x => x.IsOrphan);
    }
}

internal sealed class ProvisioningGrantConfiguration : IEntityTypeConfiguration<ProvisioningGrant>
{
    public void Configure(EntityTypeBuilder<ProvisioningGrant> builder)
    {
        builder.ToTable("ProvisioningGrants");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Permission).HasMaxLength(240).IsRequired();
        builder.HasOne<ProvisioningAccount>().WithMany().HasForeignKey(x => x.ProvisioningAccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.ProvisioningAccountId, x.Permission }).IsUnique();
        builder.HasIndex(x => x.IsRogue);
    }
}

internal sealed class ProvisioningJobConfiguration : IEntityTypeConfiguration<ProvisioningJob>
{
    public void Configure(EntityTypeBuilder<ProvisioningJob> builder)
    {
        builder.ToTable("ProvisioningJobs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ConnectorKey).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Operation).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(x => x.LastError).HasMaxLength(2000);
        builder.HasIndex(x => new { x.Status, x.RequestedAt });
        builder.HasIndex(x => new { x.UserId, x.ConnectorKey });
    }
}

internal sealed class ProvisioningQuarantineConfiguration : IEntityTypeConfiguration<ProvisioningQuarantineItem>
{
    public void Configure(EntityTypeBuilder<ProvisioningQuarantineItem> builder)
    {
        builder.ToTable("ProvisioningQuarantine");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ConnectorKey).HasMaxLength(64).IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnType("TEXT").IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(2000).IsRequired();
        builder.HasOne<ProvisioningJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.ResolvedAt, x.CreatedAt });
    }
}

internal sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> builder)
    {
        builder.ToTable("AuditRecords");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.Actor).HasMaxLength(160).IsRequired();
        builder.Property(x => x.Action).HasMaxLength(160).IsRequired();
        builder.Property(x => x.ResourceType).HasMaxLength(120).IsRequired();
        builder.Property(x => x.ResourceId).HasMaxLength(160).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.SourceIp).HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasMaxLength(500);
        builder.Property(x => x.BeforeHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.AfterHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.DetailsJson).HasColumnType("TEXT").IsRequired();
        builder.Property(x => x.PreviousHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.RecordHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => x.Sequence).IsUnique();
        builder.HasIndex(x => new { x.ResourceType, x.ResourceId, x.Timestamp });
        builder.HasIndex(x => x.CorrelationId);
    }
}
