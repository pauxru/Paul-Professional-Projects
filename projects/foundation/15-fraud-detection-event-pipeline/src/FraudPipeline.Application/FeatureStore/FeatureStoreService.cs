using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.Features;
using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Application.FeatureStore;

public sealed class FeatureStoreService
{
    private readonly FeatureStoreRuntime _runtime;
    private readonly Dictionary<string, HashSet<string>> _customersByDevice = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _devicesByCustomer = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownIps = new(StringComparer.Ordinal);
    private readonly object _crossLock = new();

    public FeatureStoreService(FeatureStoreRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>
    /// Observe a transaction and update aggregates for every touched entity
    /// (card, customer, device, ip, merchant).
    /// </summary>
    public void Observe(Transaction txn)
    {
        var wasDecline = txn.Outcome == TransactionOutcome.Declined;
        var wasChargeback = txn.Outcome == TransactionOutcome.ChargedBack || txn.Type == TransactionType.Chargeback;
        var amount = txn.Amount.Amount;

        Add(new FeatureObservation(EntityId.Of(EntityType.Card, txn.CardId), txn.OccurredAt, amount, txn.MerchantId, txn.Location.CountryIso2, txn.DeviceId, wasDecline, wasChargeback));
        Add(new FeatureObservation(EntityId.Of(EntityType.Customer, txn.CustomerId), txn.OccurredAt, amount, txn.MerchantId, txn.Location.CountryIso2, txn.DeviceId, wasDecline, wasChargeback));
        Add(new FeatureObservation(EntityId.Of(EntityType.Device, txn.DeviceId), txn.OccurredAt, amount, txn.MerchantId, txn.Location.CountryIso2, txn.DeviceId, wasDecline, wasChargeback));
        Add(new FeatureObservation(EntityId.Of(EntityType.Ip, txn.IpAddress), txn.OccurredAt, amount, txn.MerchantId, txn.Location.CountryIso2, txn.DeviceId, wasDecline, wasChargeback));
        Add(new FeatureObservation(EntityId.Of(EntityType.Merchant, txn.MerchantId), txn.OccurredAt, amount, txn.MerchantId, txn.Location.CountryIso2, txn.DeviceId, wasDecline, wasChargeback));

        // Record last location on the card entity.
        var cardState = _runtime.GetOrCreate(EntityId.Of(EntityType.Card, txn.CardId));
        lock (_runtime.LockFor(cardState.Entity))
        {
            cardState.RecordLocation(txn.Location, txn.OccurredAt);
        }

        // Cross-entity indexes for device-sharing & new-device detection.
        lock (_crossLock)
        {
            if (!_customersByDevice.TryGetValue(txn.DeviceId, out var custs))
            {
                custs = new HashSet<string>(StringComparer.Ordinal);
                _customersByDevice[txn.DeviceId] = custs;
            }
            custs.Add(txn.CustomerId);

            if (!_devicesByCustomer.TryGetValue(txn.CustomerId, out var devs))
            {
                devs = new HashSet<string>(StringComparer.Ordinal);
                _devicesByCustomer[txn.CustomerId] = devs;
            }
            devs.Add(txn.DeviceId);

            _knownIps.Add(txn.IpAddress);
        }
    }

    private void Add(FeatureObservation obs)
    {
        var state = _runtime.GetOrCreate(obs.Entity);
        lock (_runtime.LockFor(obs.Entity))
        {
            state.Add(obs);
        }
    }

    /// <summary>
    /// Rebuild the entire feature store from a replayed sequence of transactions.
    /// This is the "replayable state" property — after Rebuild, aggregate values
    /// exactly match those produced by observing the same sequence live.
    /// </summary>
    public void Rebuild(IEnumerable<Transaction> transactions)
    {
        _runtime.Reset();
        lock (_crossLock)
        {
            _customersByDevice.Clear();
            _devicesByCustomer.Clear();
            _knownIps.Clear();
        }
        foreach (var txn in transactions.OrderBy(t => t.OccurredAt))
        {
            Observe(txn);
        }
    }

    public bool WasIpEverSeen(string ip)
    {
        lock (_crossLock) return _knownIps.Contains(ip);
    }

    public int CustomersOnDevice(string deviceId)
    {
        lock (_crossLock) return _customersByDevice.TryGetValue(deviceId, out var s) ? s.Count : 0;
    }

    public int DevicesForCustomer(string customerId)
    {
        lock (_crossLock) return _devicesByCustomer.TryGetValue(customerId, out var s) ? s.Count : 0;
    }

    public bool IsNewDeviceForCustomer(string customerId, string deviceId)
    {
        lock (_crossLock)
        {
            if (!_devicesByCustomer.TryGetValue(customerId, out var s)) return true;
            return !s.Contains(deviceId);
        }
    }

    public FeatureVector Snapshot(Transaction txn, DateTimeOffset now)
    {
        var cardState = _runtime.GetOrCreate(EntityId.Of(EntityType.Card, txn.CardId));
        var custState = _runtime.GetOrCreate(EntityId.Of(EntityType.Customer, txn.CustomerId));

        WindowAggregate a1m, a5m, a1h, a24h, a7d;
        GeoLocation? lastLoc;
        DateTimeOffset? lastAt;
        lock (_runtime.LockFor(cardState.Entity))
        {
            a1m = cardState.Aggregator.Aggregate(now, 60);
            a5m = cardState.Aggregator.Aggregate(now, 300);
            a1h = cardState.Aggregator.Aggregate(now, 3600);
            a24h = cardState.Aggregator.Aggregate(now, 86400);
            a7d = cardState.Aggregator.Aggregate(now, 7L * 86400);
            lastLoc = cardState.PreviousLocation;
            lastAt = cardState.PreviousObservationAt;
        }

        return new FeatureVector(
            CardId: txn.CardId,
            CustomerId: txn.CustomerId,
            DeviceId: txn.DeviceId,
            IpAddress: txn.IpAddress,
            MerchantId: txn.MerchantId,
            TxnCount1m: a1m.Count,
            TxnCount5m: a5m.Count,
            TxnCount1h: a1h.Count,
            TxnCount24h: a24h.Count,
            TxnCount7d: a7d.Count,
            AmountSum1m: a1m.AmountSum,
            AmountSum5m: a5m.AmountSum,
            AmountSum1h: a1h.AmountSum,
            AmountSum24h: a24h.AmountSum,
            AmountSum7d: a7d.AmountSum,
            AmountAvg7d: a7d.AmountAvg,
            AmountStdDev7d: a7d.AmountStdDev,
            DistinctMerchants1h: a1h.DistinctMerchants,
            DistinctCountries24h: a24h.DistinctCountries,
            DistinctDevices24h: a24h.DistinctDevices,
            DeclineCount1h: a1h.DeclineCount,
            ChargebackCount7d: a7d.ChargebackCount,
            IsNewDevice: IsNewDeviceForCustomer(txn.CustomerId, txn.DeviceId),
            DevicesForCustomer: DevicesForCustomer(txn.CustomerId),
            CustomersForDevice: CustomersOnDevice(txn.DeviceId),
            LastLocation: lastLoc,
            LastTxnAt: lastAt);
    }

    public FeatureStoreRuntime Runtime => _runtime;
}
