using FraudPipeline.Domain.Abstractions;
using FraudPipeline.Domain.Features;
using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Application.FeatureStore;

/// <summary>
/// Rolls up windowed aggregates over all standard sub-windows for one entity.
/// The 7-day aggregator is the "master" — sub-windows (1m/5m/1h/24h) are all
/// computed from it in O(bucket-count) rather than requiring separate aggregators.
///
/// Bucket layout:
///   SliceSeconds = 60  (1-minute buckets)
///   BucketCount  = 60 * 24 * 7 = 10,080  (7-day retention)
///
/// Late-arriving events land in the correct historical bucket if it still exists;
/// otherwise they are dropped and reported via <see cref="LateEventCount"/>.
/// </summary>
public sealed class EntityFeatureState
{
    public EntityId Entity { get; }
    public WindowedFeatureAggregator Aggregator { get; }
    public DateTimeOffset? LastObservationAt { get; private set; }
    public GeoLocation? LastLocation { get; private set; }
    /// <summary>
    /// The location observed BEFORE <see cref="LastLocation"/>. Populated by
    /// <see cref="RecordLocation"/> shifting the value on every update. This
    /// is the one the impossible-travel rule reads when scoring a transaction
    /// whose own observation has already landed in <see cref="LastLocation"/>.
    /// </summary>
    public GeoLocation? PreviousLocation { get; private set; }
    public DateTimeOffset? PreviousObservationAt { get; private set; }
    public HashSet<string> KnownDevices { get; } = new(StringComparer.Ordinal);
    public HashSet<string> KnownIps { get; } = new(StringComparer.Ordinal);
    public int LateEventCount { get; private set; }

    public EntityFeatureState(EntityId entity)
    {
        Entity = entity;
        Aggregator = new WindowedFeatureAggregator(entity, sliceSeconds: 60, bucketCount: 60 * 24 * 7);
    }

    public bool ObserveDeviceIsNew(string deviceId) => KnownDevices.Add(deviceId);
    public bool ObserveIpIsNew(string ip) => KnownIps.Add(ip);

    public void Add(FeatureObservation obs)
    {
        // Reject observations older than window retention.
        if (LastObservationAt is DateTimeOffset last && obs.At < last)
        {
            var lag = last - obs.At;
            if (lag.TotalSeconds > Aggregator.WindowSeconds)
            {
                LateEventCount++;
                return;
            }
        }
        Aggregator.Add(obs);
        LastObservationAt = obs.At;
    }

    public void RecordLocation(GeoLocation location, DateTimeOffset at)
    {
        // Shift the previous window so impossible-travel can read the location
        // observed *before* the transaction currently being scored — see the
        // XML doc on PreviousLocation. Without this shift, scoring reads back
        // the current transaction's own location and impossible-travel always
        // computes zero distance.
        PreviousLocation = LastLocation;
        PreviousObservationAt = LastObservationAt;
        LastLocation = location;
        LastObservationAt = at;
    }
}

/// <summary>
/// The runtime feature store — one <see cref="EntityFeatureState"/> per (type,id).
/// Concurrency: guarded by a per-entity lock; reads under the same lock.
/// </summary>
public sealed class FeatureStoreRuntime
{
    private readonly Dictionary<string, EntityFeatureState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _locks = new(StringComparer.Ordinal);
    private readonly object _dictLock = new();
    private readonly IClock _clock;

    public FeatureStoreRuntime(IClock clock)
    {
        _clock = clock;
    }

    public EntityFeatureState GetOrCreate(EntityId entity)
    {
        lock (_dictLock)
        {
            if (!_states.TryGetValue(entity.Composite, out var s))
            {
                s = new EntityFeatureState(entity);
                _states[entity.Composite] = s;
                _locks[entity.Composite] = new object();
            }
            return s;
        }
    }

    public object LockFor(EntityId entity)
    {
        lock (_dictLock)
        {
            if (!_locks.TryGetValue(entity.Composite, out var l))
            {
                l = new object();
                _locks[entity.Composite] = l;
            }
            return l;
        }
    }

    public int EntityCount
    {
        get { lock (_dictLock) return _states.Count; }
    }

    public IEnumerable<EntityFeatureState> Snapshot()
    {
        lock (_dictLock) return _states.Values.ToArray();
    }

    public bool TryGet(EntityId entity, out EntityFeatureState state)
    {
        lock (_dictLock)
        {
            if (_states.TryGetValue(entity.Composite, out var s))
            {
                state = s;
                return true;
            }
            state = null!;
            return false;
        }
    }

    public void Reset()
    {
        lock (_dictLock)
        {
            _states.Clear();
            _locks.Clear();
        }
    }

    public int TotalBuckets()
    {
        var total = 0;
        foreach (var s in Snapshot()) total += s.Aggregator.BucketCountLive;
        return total;
    }
}
