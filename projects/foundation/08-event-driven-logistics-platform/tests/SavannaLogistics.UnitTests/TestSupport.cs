using SavannaLogistics.Application;
using SavannaLogistics.Domain;

namespace SavannaLogistics.UnitTests;

internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = now;
    public void Advance(TimeSpan by) => UtcNow += by;
    public void Set(DateTimeOffset value) => UtcNow = value;
}

internal static class TestData
{
    public static VehiclePing Ping(
        Guid vehicleId,
        long sequence,
        DateTimeOffset timestamp,
        double latitude = -1.286389,
        double longitude = 36.817223,
        double speed = 40,
        bool ignition = true,
        string? hash = null) =>
        new(
            Guid.NewGuid(),
            vehicleId,
            new GeoPoint(latitude, longitude),
            speed,
            90,
            10_000 + sequence,
            70,
            ignition,
            timestamp,
            sequence,
            timestamp,
            hash ?? $"HASH-{sequence}");
}
