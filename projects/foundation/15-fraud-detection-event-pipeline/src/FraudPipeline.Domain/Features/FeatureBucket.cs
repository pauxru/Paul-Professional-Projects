namespace FraudPipeline.Domain.Features;

/// <summary>
/// A single bucket of aggregated activity within a fixed time slice for one entity.
/// </summary>
public sealed class FeatureBucket
{
    public long SliceStartTicks { get; }
    public int Count { get; private set; }
    public decimal AmountSum { get; private set; }
    public decimal AmountSquareSum { get; private set; }
    public int DeclineCount { get; private set; }
    public int ChargebackCount { get; private set; }
    public HashSet<string> DistinctMerchants { get; } = new(StringComparer.Ordinal);
    public HashSet<string> DistinctCountries { get; } = new(StringComparer.Ordinal);
    public HashSet<string> DistinctDevices { get; } = new(StringComparer.Ordinal);

    public FeatureBucket(long sliceStartTicks) => SliceStartTicks = sliceStartTicks;

    public void Add(decimal amount, string merchantId, string countryIso2, string deviceId, bool wasDecline, bool wasChargeback)
    {
        Count++;
        AmountSum += amount;
        AmountSquareSum += amount * amount;
        if (!string.IsNullOrEmpty(merchantId)) DistinctMerchants.Add(merchantId);
        if (!string.IsNullOrEmpty(countryIso2)) DistinctCountries.Add(countryIso2);
        if (!string.IsNullOrEmpty(deviceId)) DistinctDevices.Add(deviceId);
        if (wasDecline) DeclineCount++;
        if (wasChargeback) ChargebackCount++;
    }
}
