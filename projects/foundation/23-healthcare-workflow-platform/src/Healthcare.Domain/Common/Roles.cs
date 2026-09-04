namespace Healthcare.Domain.Common;

/// <summary>Portfolio roles used for RBAC.</summary>
public static class Roles
{
    public const string Receptionist = "Receptionist";
    public const string Nurse = "Nurse";
    public const string Clinician = "Clinician";
    public const string ClinicalLead = "ClinicalLead";
    public const string Administrator = "Administrator";
    public const string BillingClerk = "BillingClerk";
    public const string Auditor = "Auditor";

    public static readonly IReadOnlyCollection<string> All = new[]
    {
        Receptionist, Nurse, Clinician, ClinicalLead, Administrator, BillingClerk, Auditor
    };
}

/// <summary>Well-known authorization policy names.</summary>
public static class Policies
{
    public const string CanReadClinicalRecord = "CanReadClinicalRecord";
    public const string CanBookAppointment = "CanBookAppointment";
    public const string CanWriteClinicalNote = "CanWriteClinicalNote";
    public const string CanManageWorkflow = "CanManageWorkflow";
    public const string CanViewAudit = "CanViewAudit";
    public const string CanManageFacility = "CanManageFacility";
}
