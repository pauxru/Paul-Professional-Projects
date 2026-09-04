namespace Contoso.Payments.Domain.Payments;

public enum PaymentIntentStatus
{
    Requires = 0,           // Just created, awaiting authorize
    Authorized = 1,
    Captured = 2,
    Voided = 3,
    Failed = 4
}
