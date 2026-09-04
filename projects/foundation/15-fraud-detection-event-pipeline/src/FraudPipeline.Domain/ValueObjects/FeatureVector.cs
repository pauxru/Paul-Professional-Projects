namespace FraudPipeline.Domain.ValueObjects;

/// <summary>
/// Immutable snapshot of the feature vector at scoring time.
/// Numeric values are ready-to-eat aggregates from the feature store.
/// </summary>
public sealed record FeatureVector(
    string CardId,
    string CustomerId,
    string DeviceId,
    string IpAddress,
    string MerchantId,
    int TxnCount1m,
    int TxnCount5m,
    int TxnCount1h,
    int TxnCount24h,
    int TxnCount7d,
    decimal AmountSum1m,
    decimal AmountSum5m,
    decimal AmountSum1h,
    decimal AmountSum24h,
    decimal AmountSum7d,
    decimal AmountAvg7d,
    decimal AmountStdDev7d,
    int DistinctMerchants1h,
    int DistinctCountries24h,
    int DistinctDevices24h,
    int DeclineCount1h,
    int ChargebackCount7d,
    bool IsNewDevice,
    int DevicesForCustomer,
    int CustomersForDevice,
    GeoLocation? LastLocation,
    DateTimeOffset? LastTxnAt);
