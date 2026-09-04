using System.Collections.Concurrent;
using SavannaLogistics.Domain;

namespace SavannaLogistics.Application;

public sealed class GeofenceSpatialIndex
{
    private readonly double _cellSizeDegrees;
    private readonly Dictionary<(int Latitude, int Longitude), HashSet<Guid>> _cells = [];
    private readonly Dictionary<Guid, Geofence> _geofences = [];
    private readonly object _gate = new();

    public GeofenceSpatialIndex(double cellSizeDegrees = 0.05)
    {
        if (cellSizeDegrees <= 0 || cellSizeDegrees > 10)
            throw new ArgumentOutOfRangeException(nameof(cellSizeDegrees));
        _cellSizeDegrees = cellSizeDegrees;
    }

    public int GeofenceCount
    {
        get
        {
            lock (_gate) return _geofences.Count;
        }
    }

    public void Replace(IEnumerable<Geofence> geofences)
    {
        lock (_gate)
        {
            _cells.Clear();
            _geofences.Clear();
            foreach (var geofence in geofences)
            {
                _geofences[geofence.Id] = geofence;
                var box = geofence.GetBoundingBox();
                var minCell = Cell(new GeoPoint(box.MinLatitude, box.MinLongitude));
                var maxCell = Cell(new GeoPoint(box.MaxLatitude, box.MaxLongitude));
                for (var latitude = minCell.Latitude; latitude <= maxCell.Latitude; latitude++)
                {
                    for (var longitude = minCell.Longitude; longitude <= maxCell.Longitude; longitude++)
                    {
                        var key = (latitude, longitude);
                        if (!_cells.TryGetValue(key, out var ids))
                        {
                            ids = [];
                            _cells[key] = ids;
                        }

                        ids.Add(geofence.Id);
                    }
                }
            }
        }
    }

    public IReadOnlyList<Geofence> Candidates(GeoPoint point)
    {
        lock (_gate)
        {
            return _cells.TryGetValue(Cell(point), out var ids)
                ? ids.Select(id => _geofences[id]).ToArray()
                : [];
        }
    }

    public IReadOnlyList<Geofence> Containing(GeoPoint point) =>
        Candidates(point).Where(geofence => geofence.Contains(point)).ToArray();

    private (int Latitude, int Longitude) Cell(GeoPoint point) =>
        ((int)Math.Floor((point.Latitude + 90d) / _cellSizeDegrees),
         (int)Math.Floor((point.Longitude + 180d) / _cellSizeDegrees));
}

public enum BoundaryTransition
{
    None,
    Entered,
    Exited
}

public sealed class BoundaryHysteresisTracker
{
    private sealed class State
    {
        public bool StableInside;
        public bool? CandidateInside;
        public DateTimeOffset CandidateSince;
    }

    private readonly ConcurrentDictionary<string, State> _states = [];

    public BoundaryTransition Update(
        string key,
        bool measuredInside,
        DateTimeOffset observedAt,
        TimeSpan enterDwell,
        TimeSpan exitDwell)
    {
        var state = _states.GetOrAdd(key, _ => new State());
        lock (state)
        {
            if (measuredInside == state.StableInside)
            {
                state.CandidateInside = null;
                return BoundaryTransition.None;
            }

            if (state.CandidateInside != measuredInside)
            {
                state.CandidateInside = measuredInside;
                state.CandidateSince = observedAt;
            }

            var required = measuredInside ? enterDwell : exitDwell;
            if (observedAt - state.CandidateSince < required)
            {
                return BoundaryTransition.None;
            }

            state.StableInside = measuredInside;
            state.CandidateInside = null;
            return measuredInside ? BoundaryTransition.Entered : BoundaryTransition.Exited;
        }
    }

    public void Clear() => _states.Clear();
}
