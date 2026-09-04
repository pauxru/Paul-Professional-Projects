namespace FieldOps.Domain;

public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

public interface IAppendOnly;

public sealed class DomainRuleException(string message) : Exception(message);

public enum OrganizationStatus
{
    Trial,
    Active,
    PastDue,
    Suspended
}

public enum SubscriptionPlan
{
    Free,
    Starter,
    Professional,
    Enterprise
}

public enum MemberRole
{
    Owner,
    Admin,
    Dispatcher,
    Technician,
    Viewer
}

public enum AssetStatus
{
    Active,
    MaintenanceDue,
    OutOfService,
    Retired
}

public enum JobPriority
{
    Low,
    Normal,
    High,
    Critical
}

public enum JobStatus
{
    Draft,
    Scheduled,
    Dispatched,
    InProgress,
    Completed,
    Cancelled,
    Failed
}

public enum InspectionItemType
{
    Boolean,
    Number,
    Text,
    PhotoReference
}

public enum BillingEventType
{
    CustomerCreated,
    SubscriptionCreated,
    PlanChanged,
    InvoiceGenerated,
    PaymentSucceeded,
    PaymentFailed
}

public readonly record struct Money(decimal Amount, string Currency)
{
    public Money Normalize()
    {
        if (Amount < 0)
        {
            throw new DomainRuleException("Money cannot be negative.");
        }

        var currency = Currency.Trim().ToUpperInvariant();
        if (currency is not ("KES" or "USD"))
        {
            throw new DomainRuleException("Only KES and USD are supported by this demonstration.");
        }

        return new Money(decimal.Round(Amount, 2), currency);
    }
}
