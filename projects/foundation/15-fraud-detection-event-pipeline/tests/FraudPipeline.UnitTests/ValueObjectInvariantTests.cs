using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.UnitTests;

public class ValueObjectInvariantTests
{
    [Fact]
    public void Money_RejectsUnknownCurrency()
    {
        Assert.Throws<ArgumentException>(() => Money.Of(100m, "XXX"));
    }

    [Fact]
    public void Money_RejectsNegativeAmount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Of(-1m, "USD"));
    }

    [Fact]
    public void Money_AcceptsKesUsdEurGbp()
    {
        Assert.Equal("USD", Money.Of(1m, "USD").Currency);
        Assert.Equal("KES", Money.Of(1m, "KES").Currency);
        Assert.Equal("EUR", Money.Of(1m, "EUR").Currency);
        Assert.Equal("GBP", Money.Of(1m, "GBP").Currency);
    }

    [Fact]
    public void GeoLocation_RejectsOutOfRangeLatitude()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GeoLocation.Of(91.0, 0.0, "US"));
        Assert.Throws<ArgumentOutOfRangeException>(() => GeoLocation.Of(-91.0, 0.0, "US"));
    }

    [Fact]
    public void GeoLocation_RejectsOutOfRangeLongitude()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GeoLocation.Of(0.0, 181.0, "US"));
        Assert.Throws<ArgumentOutOfRangeException>(() => GeoLocation.Of(0.0, -181.0, "US"));
    }

    [Fact]
    public void GeoLocation_RejectsBadCountry()
    {
        Assert.Throws<ArgumentException>(() => GeoLocation.Of(0.0, 0.0, "USA"));
    }
}
