using FraudPipeline.Domain.Rules;
using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.UnitTests;

public class ImpossibleTravelTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Haversine_SameCity_ReturnsFeasibleAndTinyDistance()
    {
        var a = GeoLocation.Of(-1.2921, 36.8219, "KE"); // Nairobi
        var b = GeoLocation.Of(-1.2900, 36.8250, "KE");
        var result = ImpossibleTravel.Assess(a, _t0, b, _t0.AddMinutes(10));
        Assert.False(result.Impossible);
        Assert.InRange(result.DistanceKm, 0.1, 1.0);
    }

    [Fact]
    public void Haversine_AntipodalPair_ReturnsApproximately20015Km()
    {
        // Nairobi (~ -1.28, 36.82) — antipode ~ (1.28, -143.18)
        var a = GeoLocation.Of(-1.2921, 36.8219, "KE");
        var b = GeoLocation.Of(1.2921, -143.1781, "US");
        var d = a.DistanceKmTo(b);
        Assert.InRange(d, 19_800, 20_100);
    }

    [Fact]
    public void ImpossibleTravel_NairobiToNewYorkIn30Minutes_Impossible()
    {
        var nairobi = GeoLocation.Of(-1.2921, 36.8219, "KE");
        var newYork = GeoLocation.Of(40.7128, -74.0060, "US");
        var result = ImpossibleTravel.Assess(nairobi, _t0, newYork, _t0.AddMinutes(30));
        Assert.True(result.Impossible);
        Assert.True(result.SpeedKmh > 900);
    }

    [Fact]
    public void ImpossibleTravel_NairobiToNewYorkOverNightlyFlight_Feasible()
    {
        var nairobi = GeoLocation.Of(-1.2921, 36.8219, "KE");
        var newYork = GeoLocation.Of(40.7128, -74.0060, "US");
        var result = ImpossibleTravel.Assess(nairobi, _t0, newYork, _t0.AddHours(18));
        Assert.False(result.Impossible);
    }

    [Fact]
    public void ImpossibleTravel_SameLocationAtSameSecond_Feasible()
    {
        var a = GeoLocation.Of(-1.2921, 36.8219, "KE");
        var result = ImpossibleTravel.Assess(a, _t0, a, _t0);
        Assert.False(result.Impossible);
    }

    [Fact]
    public void ImpossibleTravel_SameSecondButDifferentCity_Impossible()
    {
        var nairobi = GeoLocation.Of(-1.2921, 36.8219, "KE");
        var mombasa = GeoLocation.Of(-4.0435, 39.6682, "KE");
        var result = ImpossibleTravel.Assess(nairobi, _t0, mombasa, _t0);
        Assert.True(result.Impossible);
    }
}
