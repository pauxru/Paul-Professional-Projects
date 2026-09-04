using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SavannaLogistics.Application;

public enum DeduplicationDecision
{
    New,
    Duplicate,
    Conflict
}

public sealed class ExpiringLruDeduplicator
{
    private sealed record Entry(string Hash, DateTimeOffset ExpiresAt);
    private sealed record Expiry((Guid VehicleId, long Sequence) Key, DateTimeOffset ExpiresAt);

    private readonly int _capacity;
    private readonly TimeSpan _expiry;
    private readonly IClock _clock;
    private readonly Dictionary<(Guid VehicleId, long Sequence), (Entry Entry, LinkedListNode<(Guid, long)> Node)> _entries = [];
    private readonly LinkedList<(Guid VehicleId, long Sequence)> _lru = [];
    private readonly PriorityQueue<Expiry, DateTimeOffset> _expirations = new();
    private readonly object _gate = new();

    public ExpiringLruDeduplicator(int capacity, TimeSpan expiry, IClock clock)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (expiry <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(expiry));
        _capacity = capacity;
        _expiry = expiry;
        _clock = clock;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                RemoveExpired();
                return _entries.Count;
            }
        }
    }

    public DeduplicationDecision CheckAndRemember(Guid vehicleId, long sequence, string contentHash)
    {
        var key = (vehicleId, sequence);
        lock (_gate)
        {
            RemoveExpired();
            if (_entries.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing.Node);
                _lru.AddFirst(existing.Node);
                return string.Equals(existing.Entry.Hash, contentHash, StringComparison.Ordinal)
                    ? DeduplicationDecision.Duplicate
                    : DeduplicationDecision.Conflict;
            }

            var node = new LinkedListNode<(Guid, long)>(key);
            _lru.AddFirst(node);
            var expiresAt = _clock.UtcNow.Add(_expiry);
            _entries[key] = (new Entry(contentHash, expiresAt), node);
            _expirations.Enqueue(new Expiry(key, expiresAt), expiresAt);
            while (_entries.Count > _capacity && _lru.Last is not null)
            {
                _entries.Remove(_lru.Last.Value);
                _lru.RemoveLast();
            }

            return DeduplicationDecision.New;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
            _expirations.Clear();
        }
    }

    private void RemoveExpired()
    {
        var now = _clock.UtcNow;
        while (_expirations.TryPeek(out _, out var expiryTime) && expiryTime <= now)
        {
            var expiry = _expirations.Dequeue();
            if (!_entries.TryGetValue(expiry.Key, out var current) ||
                current.Entry.ExpiresAt != expiry.ExpiresAt)
            {
                continue;
            }

            _lru.Remove(current.Node);
            _entries.Remove(expiry.Key);
        }
    }
}

public static class PingContentHasher
{
    public static string Hash(VehiclePingInput ping)
    {
        var canonical = string.Join(
            "|",
            ping.VehicleId.ToString("N"),
            ping.SequenceNumber.ToString(CultureInfo.InvariantCulture),
            ping.Latitude.ToString("R", CultureInfo.InvariantCulture),
            ping.Longitude.ToString("R", CultureInfo.InvariantCulture),
            ping.SpeedKph.ToString("R", CultureInfo.InvariantCulture),
            ping.HeadingDegrees.ToString("R", CultureInfo.InvariantCulture),
            ping.OdometerKm.ToString("R", CultureInfo.InvariantCulture),
            ping.FuelPercent.ToString("R", CultureInfo.InvariantCulture),
            ping.Ignition ? "1" : "0",
            ping.DeviceTimestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
