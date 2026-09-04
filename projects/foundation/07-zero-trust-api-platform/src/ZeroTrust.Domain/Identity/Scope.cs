namespace ZeroTrust.Domain.Identity;

public sealed class Scope
{
    public const string CustomerRead = "customer.read";
    public const string CustomerWrite = "customer.write";
    public const string PartnerPaymentsInitiate = "partner.payments.initiate";
    public const string PartnerPaymentsRead = "partner.payments.read";
    public const string PartnerStatementsRead = "partner.statements.read";
    public const string AdminAudit = "admin.audit";
    public const string AdminUsers = "admin.users";
    public const string AdminAuthzEvaluate = "admin.authz.evaluate";
    public const string InternalServiceBilling = "internal.billing";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(new[]
    {
        CustomerRead, CustomerWrite,
        PartnerPaymentsInitiate, PartnerPaymentsRead, PartnerStatementsRead,
        AdminAudit, AdminUsers, AdminAuthzEvaluate,
        InternalServiceBilling
    });

    public static bool IsKnown(string scope) => All.Contains(scope);
}
