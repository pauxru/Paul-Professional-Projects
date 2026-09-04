using SavannaLogistics.Application;
using SavannaLogistics.Domain;

namespace SavannaLogistics.UnitTests;

public sealed class TripAndGeofenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Trip_HappyLifecycle_ReachesCompleted()
    {
        var trip = NewTrip();
        trip.Start(Start);
        trip.MarkInTransit();
        trip.ArriveAtStop(0, Start.AddMinutes(10));
        trip.DepartStop(Start.AddMinutes(12), false);
        trip.ArriveAtStop(1, Start.AddMinutes(30));
        trip.DepartStop(Start.AddMinutes(32), true);
        Assert.Equal(TripStatus.Completed, trip.Status);
        Assert.Equal(1, trip.CurrentStopSequence);
        Assert.Equal(Start.AddMinutes(32), trip.ActualCompleted);
    }

    [Fact]
    public void Trip_CannotStartTwice()
    {
        var trip = NewTrip();
        trip.Start(Start);
        Assert.Throws<InvalidOperationException>(() => trip.Start(Start.AddMinutes(1)));
    }

    [Fact]
    public void Trip_RejectsArrivalForWrongStop()
    {
        var trip = NewTrip();
        trip.Start(Start);
        Assert.Throws<InvalidOperationException>(() => trip.ArriveAtStop(1, Start.AddMinutes(1)));
    }

    [Fact]
    public void Trip_AbortIsTerminal()
    {
        var trip = NewTrip();
        trip.Abort(Start);
        Assert.Equal(TripStatus.Aborted, trip.Status);
        Assert.Throws<InvalidOperationException>(() => trip.Abort(Start.AddMinutes(1)));
    }

    [Fact]
    public void Hysteresis_RequiresContinuousDwellBeforeEntry()
    {
        var tracker = new BoundaryHysteresisTracker();
        Assert.Equal(BoundaryTransition.None, tracker.Update("x", true, Start, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
        Assert.Equal(BoundaryTransition.None, tracker.Update("x", true, Start.AddSeconds(9), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
        Assert.Equal(BoundaryTransition.Entered, tracker.Update("x", true, Start.AddSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Hysteresis_GpsFlappingDoesNotCreateFalseEntry()
    {
        var tracker = new BoundaryHysteresisTracker();
        tracker.Update("x", true, Start, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        tracker.Update("x", false, Start.AddSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        Assert.Equal(BoundaryTransition.None, tracker.Update("x", true, Start.AddSeconds(8), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
        Assert.Equal(BoundaryTransition.None, tracker.Update("x", true, Start.AddSeconds(17), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
        Assert.Equal(BoundaryTransition.Entered, tracker.Update("x", true, Start.AddSeconds(18), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Hysteresis_RequiresExitDwellAfterStableEntry()
    {
        var tracker = new BoundaryHysteresisTracker();
        tracker.Update("x", true, Start, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.Equal(BoundaryTransition.None, tracker.Update("x", false, Start.AddSeconds(1), TimeSpan.Zero, TimeSpan.FromSeconds(5)));
        Assert.Equal(BoundaryTransition.Exited, tracker.Update("x", false, Start.AddSeconds(6), TimeSpan.Zero, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void CircleGeofence_BoundaryIsInside()
    {
        var centre = new GeoPoint(-1.286389, 36.817223);
        var geofence = Geofence.Circle(Guid.NewGuid(), "depot", centre, 1);
        var latitudeDelta = 1d / GeoMath.EarthRadiusKm * 180d / Math.PI;
        var nearBoundary = new GeoPoint(centre.Latitude + latitudeDelta, centre.Longitude);
        Assert.True(geofence.Contains(nearBoundary));
    }

    private static Trip NewTrip() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Start.AddMinutes(-1), Start.AddHours(2));
}
