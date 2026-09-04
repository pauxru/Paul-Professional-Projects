namespace Northstar.Iga.Domain;

public sealed class UserIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EmployeeNumber { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public Guid? ManagerId { get; set; }
    public string Location { get; set; } = string.Empty;
    public string CostCentre { get; set; } = string.Empty;
    public EmploymentType EmploymentType { get; set; }
    public int Clearance { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public IdentityStatus Status { get; set; } = IdentityStatus.Pending;
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? SessionsTerminatedAt { get; set; }
    public long Version { get; set; }

    public void Activate()
    {
        if (Status == IdentityStatus.Terminated)
        {
            throw new DomainRuleException("A terminated identity cannot be reactivated by the joiner workflow.");
        }

        Status = IdentityStatus.Active;
        Version++;
    }

    public void Terminate(DateTimeOffset at)
    {
        Status = IdentityStatus.Terminated;
        EndDate ??= DateOnly.FromDateTime(at.UtcDateTime);
        SessionsTerminatedAt = at;
        Version++;
    }
}

public sealed class TargetApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid? OwnerUserId { get; set; }
}

public sealed class Entitlement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Permission { get; set; } = string.Empty;
    public Guid? OwnerUserId { get; set; }
    public RiskRating Risk { get; set; }
    public string BusinessDescription { get; set; } = string.Empty;
    public bool IsPrivileged { get; set; }
}

public sealed class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsBirthright { get; set; }
    public string? BirthrightRule { get; set; }
}

public sealed class RoleEntitlement
{
    public Guid RoleId { get; set; }
    public Guid EntitlementId { get; set; }
}

public sealed class RoleInheritance
{
    public Guid RoleId { get; set; }
    public Guid InheritedRoleId { get; set; }
}

public sealed class UserRoleGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public GrantSource Source { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class UserEntitlementGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid EntitlementId { get; set; }
    public GrantSource Source { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class UserEntitlementExclusion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid EntitlementId { get; set; }
    public Guid? CampaignItemId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class Group
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public GroupType Type { get; set; }
    public string? DynamicRule { get; set; }
}

public sealed class GroupMember
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public GroupMembershipSource Source { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public sealed class GroupRole
{
    public Guid GroupId { get; set; }
    public Guid RoleId { get; set; }
}

public sealed class PolicyDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public PolicyEffect Effect { get; set; }
    public string PermissionPattern { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string ConditionsJson { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
}

public sealed class SoDRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid EntitlementAId { get; set; }
    public Guid EntitlementBId { get; set; }
    public RiskRating Severity { get; set; } = RiskRating.High;
    public string BusinessDescription { get; set; } = string.Empty;
}

public sealed class SoDException
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public Guid UserId { get; set; }
    public string ApprovedBy { get; set; } = string.Empty;
    public string Justification { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class AccessRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid RequesterId { get; set; }
    public RequestTargetType TargetType { get; set; }
    public Guid TargetId { get; set; }
    public string Justification { get; set; } = string.Empty;
    public int? DurationDays { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public AccessRequestStatus Status { get; set; } = AccessRequestStatus.Pending;
    public RiskRating Risk { get; set; }
    public int CurrentStage { get; set; }
    public string? RejectionReason { get; set; }
}

public sealed class ApprovalStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public int Stage { get; set; }
    public string Kind { get; set; } = string.Empty;
    public Guid ApproverId { get; set; }
    public Guid OriginalApproverId { get; set; }
    public ApprovalStepStatus Status { get; set; } = ApprovalStepStatus.Waiting;
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Reason { get; set; }
    public bool WasDelegated { get; set; }
    public bool WasEscalated { get; set; }
}

public sealed class Elevation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid EntitlementId { get; set; }
    public string Justification { get; set; } = string.Empty;
    public string TicketReference { get; set; } = string.Empty;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public ElevationStatus Status { get; set; }
    public bool RequiresApproval { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string? SessionRecordingReference { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class CertificationCampaign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string ScopeType { get; set; } = string.Empty;
    public string ScopeValue { get; set; } = string.Empty;
    public ReviewerMode ReviewerMode { get; set; }
    public DateTimeOffset Deadline { get; set; }
    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class CertificationItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Guid UserId { get; set; }
    public Guid EntitlementId { get; set; }
    public Guid ReviewerId { get; set; }
    public CertificationDecision Decision { get; set; } = CertificationDecision.Pending;
    public string DerivationJson { get; set; } = "[]";
    public string? Justification { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

public sealed class LifecycleWorkflow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public WorkflowStatus Status { get; set; } = WorkflowStatus.Running;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class LifecycleWorkflowStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public WorkflowStepStatus Status { get; set; } = WorkflowStepStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class ProvisioningAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ConnectorKey { get; set; } = string.Empty;
    public Guid ApplicationId { get; set; }
    public Guid? UserId { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool IsOrphan { get; set; }
    public DateTimeOffset LastSynchronizedAt { get; set; }
}

public sealed class ProvisioningGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProvisioningAccountId { get; set; }
    public Guid? EntitlementId { get; set; }
    public string Permission { get; set; } = string.Empty;
    public bool IsRogue { get; set; }
}

public sealed class ProvisioningJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ConnectorKey { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public ProvisioningOperation Operation { get; set; }
    public ProvisioningJobStatus Status { get; set; } = ProvisioningJobStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class ProvisioningQuarantineItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public string ConnectorKey { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class AuditRecord
{
    public long Id { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string? SourceIp { get; set; }
    public string? UserAgent { get; set; }
    public string BeforeHash { get; set; } = string.Empty;
    public string AfterHash { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
    public string PreviousHash { get; set; } = string.Empty;
    public string RecordHash { get; set; } = string.Empty;
}
