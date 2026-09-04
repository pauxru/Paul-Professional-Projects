using Healthcare.Domain.Common;

namespace Healthcare.Domain.Facilities;

public sealed class Facility : Entity
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string TimeZoneId { get; private set; } = "UTC";

    private readonly List<Room> _rooms = new();
    public IReadOnlyCollection<Room> Rooms => _rooms.AsReadOnly();

    private readonly List<OperatingHours> _hours = new();
    public IReadOnlyCollection<OperatingHours> Hours => _hours.AsReadOnly();

    private readonly List<Closure> _closures = new();
    public IReadOnlyCollection<Closure> Closures => _closures.AsReadOnly();

    private Facility() { }

    public static Facility Create(string code, string name, string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new DomainException("facility.code.required", "Facility code required.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("facility.name.required", "Facility name required.");
        // Validate tz id
        _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return new Facility { Code = code.Trim().ToUpperInvariant(), Name = name.Trim(), TimeZoneId = timeZoneId };
    }

    public Room AddRoom(string name, IEnumerable<string> capabilities)
    {
        var room = new Room(this.Id, name, capabilities);
        _rooms.Add(room);
        return room;
    }

    public void SetHours(DayOfWeek day, TimeOnly open, TimeOnly close)
    {
        if (close <= open) throw new DomainException("facility.hours.invalid", "Close time must be after open time.");
        var existing = _hours.FirstOrDefault(h => h.Day == day);
        if (existing is null) _hours.Add(new OperatingHours(this.Id, day, open, close));
        else { existing.Open = open; existing.Close = close; }
    }

    public void AddClosure(DateOnly date, string reason)
    {
        if (!_closures.Any(c => c.Date == date))
            _closures.Add(new Closure(this.Id, date, reason));
    }

    public bool IsOpenAtLocalDay(DateOnly localDate, out TimeOnly open, out TimeOnly close)
    {
        open = default; close = default;
        if (_closures.Any(c => c.Date == localDate)) return false;
        var hours = _hours.FirstOrDefault(h => h.Day == localDate.DayOfWeek);
        if (hours is null) return false;
        open = hours.Open; close = hours.Close;
        return true;
    }
}

public sealed class Room
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid FacilityId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string CapabilitiesCsv { get; private set; } = string.Empty;

    private Room() { }

    internal Room(Guid facilityId, string name, IEnumerable<string> capabilities)
    {
        FacilityId = facilityId;
        Name = name;
        CapabilitiesCsv = string.Join(",", capabilities.Select(c => c.Trim().ToLowerInvariant()));
    }

    public IReadOnlyCollection<string> Capabilities =>
        CapabilitiesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool HasCapability(string cap) =>
        Capabilities.Any(c => string.Equals(c, cap, StringComparison.OrdinalIgnoreCase));
}

public sealed class OperatingHours
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid FacilityId { get; private set; }
    public DayOfWeek Day { get; private set; }
    public TimeOnly Open { get; internal set; }
    public TimeOnly Close { get; internal set; }

    private OperatingHours() { }

    internal OperatingHours(Guid facilityId, DayOfWeek day, TimeOnly open, TimeOnly close)
    {
        FacilityId = facilityId;
        Day = day;
        Open = open;
        Close = close;
    }
}

public sealed class Closure
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid FacilityId { get; private set; }
    public DateOnly Date { get; private set; }
    public string Reason { get; private set; } = string.Empty;

    private Closure() { }

    internal Closure(Guid facilityId, DateOnly date, string reason)
    {
        FacilityId = facilityId;
        Date = date;
        Reason = reason;
    }
}
