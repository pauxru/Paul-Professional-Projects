namespace FraudPipeline.Domain.Rules;

/// <summary>
/// MCC (Merchant Category Code) risk weighting. Kept as a small, curated table
/// rather than a general ML embedding for explainability. High-risk categories
/// contribute more to the aggregate score.
/// </summary>
public static class MccRisk
{
    private static readonly Dictionary<string, int> _weights = new(StringComparer.Ordinal)
    {
        // High risk — money transfer, wire, gambling, crypto exchanges (illustrative)
        { "6051", 90 }, // Non-financial currency (crypto/quasi-cash)
        { "4829", 85 }, // Money transfer
        { "6011", 70 }, // ATM cash disbursement
        { "7995", 80 }, // Gambling
        { "5967", 75 }, // Direct marketing / dating
        // Medium risk
        { "5411", 15 }, // Grocery
        { "5812", 20 }, // Eating places / restaurants
        { "5541", 25 }, // Service stations
        { "4111", 20 }, // Transportation - passenger
        { "4816", 30 }, // Digital goods
        // Low risk
        { "5912", 10 }, // Pharmacies
        { "8062", 5 },  // Hospitals
        { "8220", 5 },  // Universities
    };

    public static int Weight(string mcc) => _weights.TryGetValue(mcc, out var w) ? w : 20;

    public static bool IsHighRisk(string mcc) => Weight(mcc) >= 70;
}
