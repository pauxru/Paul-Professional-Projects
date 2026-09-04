using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Domain.Entities;

public enum TransactionType
{
    CardPresent = 1,
    CardNotPresent = 2,
    MobileMoney = 3,
    Refund = 4,
    Chargeback = 5
}

public enum TransactionOutcome
{
    Pending = 0,
    Approved = 1,
    Declined = 2,
    Reversed = 3,
    ChargedBack = 4
}

/// <summary>
/// A transaction event flowing through the pipeline.
/// Constructor performs invariant validation; setters are private.
/// </summary>
public sealed class Transaction
{
    public Guid Id { get; private set; }
    public string TransactionRef { get; private set; }
    public string CardId { get; private set; }
    public string CustomerId { get; private set; }
    public string DeviceId { get; private set; }
    public string IpAddress { get; private set; }
    public string MerchantId { get; private set; }
    public string MerchantCategoryCode { get; private set; }
    public Money Amount { get; private set; } = default!;
    public TransactionType Type { get; private set; }
    public TransactionOutcome Outcome { get; private set; }
    public GeoLocation Location { get; private set; } = default!;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public bool GroundTruthFraud { get; private set; }
    public string? GroundTruthPattern { get; private set; }

    private Transaction() { CardId = CustomerId = DeviceId = IpAddress = MerchantId = MerchantCategoryCode = TransactionRef = ""; }

    public Transaction(
        Guid id,
        string transactionRef,
        string cardId,
        string customerId,
        string deviceId,
        string ipAddress,
        string merchantId,
        string mcc,
        Money amount,
        TransactionType type,
        GeoLocation location,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt,
        bool groundTruthFraud = false,
        string? groundTruthPattern = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id required.", nameof(id));
        if (string.IsNullOrWhiteSpace(transactionRef)) throw new ArgumentException("TransactionRef required.", nameof(transactionRef));
        if (string.IsNullOrWhiteSpace(cardId)) throw new ArgumentException("CardId required.", nameof(cardId));
        if (string.IsNullOrWhiteSpace(customerId)) throw new ArgumentException("CustomerId required.", nameof(customerId));
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("DeviceId required.", nameof(deviceId));
        if (string.IsNullOrWhiteSpace(ipAddress)) throw new ArgumentException("IpAddress required.", nameof(ipAddress));
        if (string.IsNullOrWhiteSpace(merchantId)) throw new ArgumentException("MerchantId required.", nameof(merchantId));
        if (string.IsNullOrWhiteSpace(mcc) || mcc.Length != 4) throw new ArgumentException("MCC must be a 4-digit code.", nameof(mcc));
        if (receivedAt < occurredAt) throw new ArgumentException("ReceivedAt cannot be before OccurredAt.", nameof(receivedAt));

        Id = id;
        TransactionRef = transactionRef;
        CardId = cardId;
        CustomerId = customerId;
        DeviceId = deviceId;
        IpAddress = ipAddress;
        MerchantId = merchantId;
        MerchantCategoryCode = mcc;
        Amount = amount;
        Type = type;
        Location = location;
        OccurredAt = occurredAt;
        ReceivedAt = receivedAt;
        Outcome = TransactionOutcome.Pending;
        GroundTruthFraud = groundTruthFraud;
        GroundTruthPattern = groundTruthPattern;
    }

    public void SetOutcome(TransactionOutcome outcome) => Outcome = outcome;
}
