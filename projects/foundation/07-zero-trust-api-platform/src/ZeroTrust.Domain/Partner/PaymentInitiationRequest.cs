using ZeroTrust.Domain.Common;

namespace ZeroTrust.Domain.Partner;

public sealed class PaymentInitiationRequest : Entity
{
    public string ExternalReference { get; private set; } = default!;
    public string PartnerCode { get; private set; } = default!;
    public string DebtorAccountNumber { get; private set; } = default!;
    public string CreditorAccountNumber { get; private set; } = default!;
    public decimal AmountMinorUnits { get; private set; }
    public string Currency { get; private set; } = "USD";
    public string Status { get; private set; } = "Accepted";
    public string SchemeSignatureVersion { get; private set; } = "v1";
    public string CorrelationId { get; private set; } = default!;

    private PaymentInitiationRequest() { }

    public PaymentInitiationRequest(string externalReference, string partnerCode, string debtor, string creditor,
        decimal amountMinorUnits, string currency, string schemeVersion, string correlationId)
    {
        if (amountMinorUnits <= 0) throw new DomainException("amount must be positive");
        ExternalReference = externalReference;
        PartnerCode = partnerCode;
        DebtorAccountNumber = debtor;
        CreditorAccountNumber = creditor;
        AmountMinorUnits = amountMinorUnits;
        Currency = currency;
        SchemeSignatureVersion = schemeVersion;
        CorrelationId = correlationId;
    }

    public void MarkRejected() => Status = "Rejected";
}
