using Healthcare.Domain.Common;

namespace Healthcare.Domain.Appointments;

/// <summary>Encounter/appointment lifecycle states.</summary>
public enum AppointmentStatus
{
    Booked = 0,
    Confirmed = 1,
    CheckedIn = 2,
    InTriage = 3,
    WithClinician = 4,
    AwaitingResults = 5,
    Completed = 6,
    NoShow = 7,
    Cancelled = 8
}

public sealed class AppointmentType : Entity
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public int DurationMinutes { get; private set; }
    public int BufferMinutes { get; private set; }
    public string RequiredRoomCapability { get; private set; } = string.Empty;
    public bool AllowsOverbooking { get; private set; }

    private AppointmentType() { }

    public static AppointmentType Create(string code, string name, int durationMinutes,
        int bufferMinutes, string requiredRoomCapability, bool allowsOverbooking = false)
    {
        if (durationMinutes < 5 || durationMinutes > 240)
            throw new DomainException("apptype.duration.invalid", "Duration must be 5..240 minutes.");
        if (bufferMinutes < 0 || bufferMinutes > 60)
            throw new DomainException("apptype.buffer.invalid", "Buffer must be 0..60 minutes.");
        return new AppointmentType
        {
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            DurationMinutes = durationMinutes,
            BufferMinutes = bufferMinutes,
            RequiredRoomCapability = requiredRoomCapability.Trim().ToLowerInvariant(),
            AllowsOverbooking = allowsOverbooking
        };
    }
}
