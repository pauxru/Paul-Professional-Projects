using Northstar.Iga.Domain;

namespace Northstar.Iga.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface ITokenIssuer
{
    string Issue(string subject, IEnumerable<string> scopes, TimeSpan lifetime);
}

public interface IAuditContextAccessor
{
    string? SourceIp { get; }
    string? UserAgent { get; }
}

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public sealed record CreateUserCommand(
    string EmployeeNumber,
    string DisplayName,
    string Email,
    string Department,
    string JobTitle,
    Guid? ManagerId,
    string Location,
    string CostCentre,
    EmploymentType EmploymentType,
    int Clearance,
    DateOnly StartDate,
    DateOnly? EndDate = null);

public sealed record MoveUserCommand(
    string Department,
    string JobTitle,
    Guid? ManagerId,
    string Location,
    string CostCentre,
    EmploymentType EmploymentType,
    int Clearance,
    int? GracePeriodDays = null);

public sealed record CreateApplicationCommand(string Key, string Name, string Description, Guid? OwnerUserId);
public sealed record CreateEntitlementCommand(
    Guid ApplicationId,
    string Key,
    string Permission,
    Guid? OwnerUserId,
    RiskRating Risk,
    string BusinessDescription,
    bool IsPrivileged);
public sealed record CreateRoleCommand(string Key, string Name, string Description, bool IsBirthright, string? BirthrightRule);
public sealed record CreateGroupCommand(string Key, string Name, string Description, GroupType Type, string? DynamicRule);
public sealed record CreatePolicyCommand(
    Guid? Id,
    string Name,
    string Description,
    PolicyEffect Effect,
    string PermissionPattern,
    int Priority,
    string ConditionsJson,
    bool Enabled = true);
public sealed record AuthorizationEvaluationCommand(
    Guid UserId,
    string Permission,
    IReadOnlyDictionary<string, object?>? Resource,
    IReadOnlyDictionary<string, object?>? Environment);
public sealed record PolicySimulationCommand(
    CreatePolicyCommand ProposedPolicy,
    string Permission,
    IReadOnlyDictionary<string, object?>? Resource,
    IReadOnlyDictionary<string, object?>? Environment);
public sealed record PolicySimulationResult(
    IReadOnlyList<Guid> GainedUserIds,
    IReadOnlyList<Guid> LostUserIds,
    int UnchangedAllowed,
    int UnchangedDenied);
public sealed record CreateSoDRuleCommand(
    string Name,
    Guid EntitlementAId,
    Guid EntitlementBId,
    RiskRating Severity,
    string BusinessDescription);
public sealed record CreateSoDExceptionCommand(
    Guid RuleId,
    Guid UserId,
    string ApprovedBy,
    string Justification,
    DateTimeOffset ExpiresAt);
public sealed record CreateAccessRequestCommand(
    Guid UserId,
    Guid RequesterId,
    RequestTargetType TargetType,
    Guid TargetId,
    string Justification,
    int? DurationDays);
public sealed record ApprovalDecisionCommand(Guid ActorId, string Reason);
public sealed record DelegateApprovalCommand(Guid ActorId, Guid DelegateId);
public sealed record CreateElevationCommand(
    Guid UserId,
    Guid EntitlementId,
    string Justification,
    string TicketReference,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool RequiresApproval,
    string? SessionRecordingReference);
public sealed record CreateCampaignCommand(
    string Name,
    string ScopeType,
    string ScopeValue,
    ReviewerMode ReviewerMode,
    DateTimeOffset Deadline);
public sealed record CertificationCommand(
    IReadOnlyList<Guid> ItemIds,
    CertificationDecision Decision,
    string Justification,
    Guid ReviewerId);
public sealed record CampaignProgress(
    Guid CampaignId,
    int Total,
    int Pending,
    int Approved,
    int Revoked,
    int AutoRevoked,
    decimal CompletionPercent);
public sealed record AccessProfileEntry(
    Guid EntitlementId,
    string EntitlementKey,
    string Permission,
    RiskRating Risk,
    bool Privileged,
    IReadOnlyList<IReadOnlyList<string>> DerivationPaths);
public sealed record UserAccessProfile(
    Guid UserId,
    string DisplayName,
    IReadOnlyList<AccessProfileEntry> Access);
public sealed record ReconciliationIssue(
    string ConnectorKey,
    string Kind,
    string ExternalId,
    Guid? UserId,
    string Detail);
public sealed record ReconciliationResult(
    IReadOnlyList<ReconciliationIssue> Orphans,
    IReadOnlyList<ReconciliationIssue> RogueGrants);
public sealed record ProvisioningResult(Guid JobId, ProvisioningJobStatus Status, int Attempts, string? Error);
public sealed record TokenRequest(string Subject, string[] Scopes);

public interface IIgaService
{
    Task<PagedResult<UserIdentity>> GetUsersAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<UserIdentity?> GetUserAsync(Guid id, CancellationToken cancellationToken);
    Task<UserIdentity> CreateUserAsync(CreateUserCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task<LifecycleWorkflow> ProcessJoinerAsync(Guid userId, string actor, string correlationId, CancellationToken cancellationToken);
    Task<LifecycleWorkflow> ProcessMoverAsync(Guid userId, MoveUserCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task<LifecycleWorkflow> ProcessLeaverAsync(Guid userId, string actor, string correlationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken cancellationToken);
    Task<Group> CreateGroupAsync(CreateGroupCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task AddGroupMemberAsync(Guid groupId, Guid userId, string actor, string correlationId, CancellationToken cancellationToken);
    Task AddGroupRoleAsync(Guid groupId, Guid roleId, string actor, string correlationId, CancellationToken cancellationToken);
    Task ReevaluateDynamicGroupsAsync(Guid userId, string actor, string correlationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken cancellationToken);
    Task<Role> CreateRoleAsync(CreateRoleCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task AddRoleEntitlementAsync(Guid roleId, Guid entitlementId, string actor, string correlationId, CancellationToken cancellationToken);
    Task AddRoleInheritanceAsync(Guid roleId, Guid inheritedRoleId, string actor, string correlationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TargetApplication>> GetApplicationsAsync(CancellationToken cancellationToken);
    Task<TargetApplication> CreateApplicationAsync(CreateApplicationCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Entitlement>> GetEntitlementsAsync(CancellationToken cancellationToken);
    Task<Entitlement> CreateEntitlementAsync(CreateEntitlementCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task GrantEntitlementAsync(Guid userId, Guid entitlementId, GrantSource source, string reason, DateTimeOffset? expiresAt, string actor, string correlationId, CancellationToken cancellationToken);
    Task GrantRoleAsync(Guid userId, Guid roleId, GrantSource source, string reason, DateTimeOffset? expiresAt, string actor, string correlationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PolicyDefinition>> GetPoliciesAsync(CancellationToken cancellationToken);
    Task<PolicyDefinition> CreatePolicyAsync(CreatePolicyCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task<AuthorizationDecision> EvaluateAsync(AuthorizationEvaluationCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task<PolicySimulationResult> SimulatePolicyAsync(PolicySimulationCommand command, CancellationToken cancellationToken);

    Task<SoDRule> CreateSoDRuleAsync(CreateSoDRuleCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task<SoDException> CreateSoDExceptionAsync(CreateSoDExceptionCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SoDViolation>> ScanSoDAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AccessRequest>> GetRequestsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ApprovalStep>> GetApprovalStepsAsync(Guid requestId, CancellationToken cancellationToken);
    Task<AccessRequest> CreateAccessRequestAsync(CreateAccessRequestCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task<AccessRequest> ApproveRequestAsync(Guid requestId, Guid stepId, ApprovalDecisionCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task<AccessRequest> RejectRequestAsync(Guid requestId, Guid stepId, ApprovalDecisionCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task DelegateApprovalAsync(Guid requestId, Guid stepId, DelegateApprovalCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task<int> EscalateOverdueApprovalsAsync(Guid escalationApproverId, string actor, string correlationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Elevation>> GetElevationsAsync(CancellationToken cancellationToken);
    Task<Elevation> CreateElevationAsync(CreateElevationCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task<Elevation> ApproveElevationAsync(Guid elevationId, Guid approverId, string actor, string correlationId, CancellationToken cancellationToken);
    Task<Elevation> RevokeElevationAsync(Guid elevationId, string reason, string actor, string correlationId, CancellationToken cancellationToken);
    Task<int> ExpireElevationsAsync(string actor, string correlationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CertificationCampaign>> GetCampaignsAsync(CancellationToken cancellationToken);
    Task<CertificationCampaign> CreateCampaignAsync(CreateCampaignCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CertificationItem>> GetCampaignItemsAsync(Guid campaignId, CancellationToken cancellationToken);
    Task<CampaignProgress> CertifyItemsAsync(Guid campaignId, CertificationCommand command, string actor, string correlationId, CancellationToken cancellationToken);
    Task<int> AutoRevokeOverdueCampaignsAsync(string actor, string correlationId, CancellationToken cancellationToken);
    Task<CampaignProgress> GetCampaignProgressAsync(Guid campaignId, CancellationToken cancellationToken);

    Task<ProvisioningResult> ProvisionUserAsync(Guid userId, string connectorKey, ProvisioningOperation operation, string actor, string correlationId, CancellationToken cancellationToken);
    Task<int> ImportHrIdentitiesAsync(string actor, string correlationId, CancellationToken cancellationToken);
    Task<ReconciliationResult> ReconcileAsync(string? connectorKey, string actor, string correlationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProvisioningQuarantineItem>> GetQuarantineAsync(CancellationToken cancellationToken);

    Task<UserAccessProfile> GetUserAccessProfileAsync(Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<object>> GetEntitlementHoldersReportAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<object>> GetPrivilegedInventoryReportAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<object>> GetDormantAccountsReportAsync(int dormantDays, CancellationToken cancellationToken);
    Task<IReadOnlyList<object>> GetOrphanAccountsReportAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditRecord>> GetAuditAsync(int page, int pageSize, CancellationToken cancellationToken);
}
