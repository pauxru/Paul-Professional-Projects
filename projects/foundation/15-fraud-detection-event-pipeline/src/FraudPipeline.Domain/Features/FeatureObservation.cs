using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Domain.Features;

/// <summary>
/// A single observation added to the feature store.
/// </summary>
public sealed record FeatureObservation(
    EntityId Entity,
    DateTimeOffset At,
    decimal Amount,
    string MerchantId,
    string CountryIso2,
    string DeviceId,
    bool WasDecline = false,
    bool WasChargeback = false);
