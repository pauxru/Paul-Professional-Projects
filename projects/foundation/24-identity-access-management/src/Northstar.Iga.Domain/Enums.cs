namespace Northstar.Iga.Domain;

public enum IdentityStatus { Pending, Active, Suspended, Terminated }
public enum EmploymentType { Employee, Contractor, Vendor }
public enum RiskRating { Low = 1, Medium = 2, High = 3, Critical = 4 }
public enum GroupType { Static, Dynamic }
public enum GroupMembershipSource { Static, Dynamic }
public enum PolicyEffect { Allow, Deny }
public enum GrantSource { Direct, Birthright, Request, Elevation }
public enum RequestTargetType { Role, Entitlement, Group }
public enum AccessRequestStatus { Pending, Approved, Rejected, Fulfilled, Cancelled }
public enum ApprovalStepStatus { Waiting, Pending, Approved, Rejected }
public enum ElevationStatus { Pending, Active, Expired, Revoked, Rejected }
public enum CampaignStatus { Draft, Active, Completed, Overdue }
public enum CertificationDecision { Pending, Approved, Revoked, AutoRevoked }
public enum ReviewerMode { Manager, EntitlementOwner }
public enum WorkflowStatus { Running, Completed, Failed }
public enum WorkflowStepStatus { Pending, Running, Succeeded, Failed }
public enum ProvisioningOperation { Create, Update, Disable, Delete }
public enum ProvisioningJobStatus { Pending, Succeeded, Failed, Quarantined }

public sealed class DomainRuleException(string message) : Exception(message);
