using SavannaLogistics.Application;
using SavannaLogistics.Domain;

namespace SavannaLogistics.UnitTests;

public sealed class EtaAlertProjectionTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);
    private readonly Guid _vehicle = Guid.NewGuid();

    [Fact]
    public void Eta_AsVehicleApproachesStop_RemainingTimeIsMonotonic()
    {
        var calculator = Calculator();
        var stop = new RouteStop(Guid.NewGuid(), Guid.NewGuid(), 0, "stop", new GeoPoint(-1.2, 36.9), 0.2, 0);
        var far = calculator.Compute(_vehicle, new GeoPoint(-1.3, 36.8), [stop], 50, Start);
        var near = calculator.Compute(_vehicle, new GeoPoint(-1.21, 36.89), [stop], 50, Start);
        Assert.True(near.RemainingDistanceKm < far.RemainingDistanceKm);
        Assert.True(near.FinalArrival < far.FinalArrival);
    }

    [Fact]
    public void Eta_RecomputesUsingRollingSpeed()
    {
        var calculator = Calculator();
        var stop = new RouteStop(Guid.NewGuid(), Guid.NewGuid(), 0, "stop", new GeoPoint(-1.2, 36.9), 0.2, 0);
        var slow = calculator.Compute(_vehicle, new GeoPoint(-1.3, 36.8), [stop], 20, Start);
        calculator.Clear();
        var fast = calculator.Compute(_vehicle, new GeoPoint(-1.3, 36.8), [stop], 80, Start);
        Assert.True(fast.FinalArrival < slow.FinalArrival);
    }

    [Fact]
    public void Eta_IncludesDwellAllowanceBeforeFinalStop()
    {
        var calculator = Calculator();
        var route = Guid.NewGuid();
        var first = new RouteStop(Guid.NewGuid(), route, 0, "first", new GeoPoint(-1.25, 36.85), 0.2, 10);
        var final = new RouteStop(Guid.NewGuid(), route, 1, "final", new GeoPoint(-1.2, 36.9), 0.2, 0);
        var eta = calculator.Compute(_vehicle, new GeoPoint(-1.3, 36.8), [first, final], 60, Start);
        var travelOnlyHours = eta.RemainingDistanceKm / eta.EffectiveSpeedKph;
        Assert.True(eta.FinalArrival - Start > TimeSpan.FromHours(travelOnlyHours));
    }

    [Fact]
    public void Alerts_SpeedingAndHarshBraking_AreDetected()
    {
        var engine = Engine();
        engine.Evaluate(TestData.Ping(_vehicle, 1, Start, speed: 100), null, null, null);
        var alerts = engine.Evaluate(TestData.Ping(_vehicle, 2, Start.AddSeconds(2), speed: 50), null, null, null);
        Assert.Contains(alerts, alert => alert.Type == AlertType.HarshBraking);
        Assert.Contains(engine.Evaluate(TestData.Ping(_vehicle, 3, Start.AddSeconds(3), speed: 100), null, null, null),
            alert => alert.Type == AlertType.Speeding);
    }

    [Fact]
    public void Alerts_ProlongedStop_IsDetectedAfterThreshold()
    {
        var engine = Engine();
        Assert.DoesNotContain(
            engine.Evaluate(TestData.Ping(_vehicle, 1, Start, speed: 0), null, null, null),
            alert => alert.Type == AlertType.ProlongedStop);
        Assert.Contains(
            engine.Evaluate(TestData.Ping(_vehicle, 2, Start.AddSeconds(31), speed: 0), null, null, null),
            alert => alert.Type == AlertType.ProlongedStop);
    }

    [Fact]
    public void Alerts_RouteDeviation_HasTrueAndFalseCorridorResults()
    {
        var route = new RoutePlan(
            Guid.NewGuid(),
            "corridor",
            [new GeoPoint(-1.3, 36.8), new GeoPoint(-1.2, 36.9)]);
        var engine = Engine();
        Assert.DoesNotContain(
            engine.Evaluate(TestData.Ping(_vehicle, 1, Start, -1.25, 36.85), route, null, null),
            alert => alert.Type == AlertType.RouteDeviation);
        Assert.Contains(
            engine.Evaluate(TestData.Ping(_vehicle, 2, Start.AddSeconds(1), -1.1, 36.7), route, null, null),
            alert => alert.Type == AlertType.RouteDeviation);
    }

    [Fact]
    public void Alerts_EtaPastSla_IsDetected()
    {
        var trip = new Trip(Guid.NewGuid(), _vehicle, Guid.NewGuid(), Start, Start.AddHours(1));
        trip.Start(Start);
        var alerts = Engine().Evaluate(
            TestData.Ping(_vehicle, 1, Start),
            null,
            trip,
            Start.AddHours(2));
        Assert.Contains(alerts, alert => alert.Type == AlertType.EtaBreach);
    }

    [Fact]
    public void AlertSuppression_SuppressesWithinWindowAndAllowsAfterWindow()
    {
        var suppression = new AlertSuppressionWindow(TimeSpan.FromMinutes(2));
        var candidate = new AlertCandidate(AlertType.RouteDeviation, "route", "off route");
        Assert.True(suppression.ShouldEmit(_vehicle, candidate, Start));
        Assert.False(suppression.ShouldEmit(_vehicle, candidate, Start.AddSeconds(119)));
        Assert.True(suppression.ShouldEmit(_vehicle, candidate, Start.AddSeconds(120)));
    }

    [Fact]
    public void Projection_IgnoresOldSequenceAndCanBeMarkedOfflineWithFakeClock()
    {
        var clock = new FakeClock(Start);
        var state = new VehicleState(_vehicle);
        Assert.True(state.Apply(TestData.Ping(_vehicle, 2, Start), null));
        Assert.False(state.Apply(TestData.Ping(_vehicle, 1, Start.AddSeconds(1)), null));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Single(OfflineDetector.Detect([state], clock.UtcNow, TimeSpan.FromMinutes(2)));
        Assert.Equal(VehicleStatus.Offline, state.Status);
    }

    private static EtaCalculator Calculator() =>
        new(new EtaOptions
        {
            RollingSpeedSamples = 4,
            MinimumEffectiveSpeedKph = 5,
            TrafficFactor = 1,
            DefaultDwellMinutes = 4
        });

    private static AlertRuleEngine Engine() =>
        new(new AlertOptions
        {
            SpeedLimitKph = 80,
            HarshBrakingDeltaKph = 25,
            RouteCorridorKm = 0.5,
            ProlongedStopSeconds = 30,
            SuppressionWindowSeconds = 120
        });
}
