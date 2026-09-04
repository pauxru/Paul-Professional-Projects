using JobScheduler.Domain.Scheduling;

namespace JobScheduler.UnitTests.Domain;

/// <summary>Portable timezone resolution across IANA and Windows identifiers.</summary>
public sealed class TimeZoneResolverTests
{
    [Theory]
    [InlineData("UTC")]
    [InlineData("Etc/UTC")]
    [InlineData("utc")]
    public void Utc_aliases_resolve_to_utc(string id)
    {
        Assert.Equal(TimeZoneInfo.Utc, TimeZoneResolver.Resolve(id));
    }

    [Fact]
    public void Null_or_blank_defaults_to_utc()
    {
        Assert.Equal(TimeZoneInfo.Utc, TimeZoneResolver.Resolve(null));
        Assert.Equal(TimeZoneInfo.Utc, TimeZoneResolver.Resolve("   "));
    }

    [Fact]
    public void Iana_identifier_resolves_and_supports_dst()
    {
        var tz = TimeZoneResolver.Resolve("America/New_York");
        Assert.NotNull(tz);
        Assert.True(tz.SupportsDaylightSavingTime);
    }

    [Fact]
    public void Windows_identifier_resolves_to_the_same_zone()
    {
        var byWindows = TimeZoneResolver.Resolve("Eastern Standard Time");
        var byIana = TimeZoneResolver.Resolve("America/New_York");
        // Same underlying zone: identical offset at a known instant.
        var probe = new DateTimeOffset(2024, 7, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(byIana.GetUtcOffset(probe), byWindows.GetUtcOffset(probe));
    }

    [Fact]
    public void Unknown_zone_is_invalid()
    {
        Assert.False(TimeZoneResolver.IsValid("Not/AZone"));
        Assert.Throws<TimeZoneNotFoundException>(() => TimeZoneResolver.Resolve("Not/AZone"));
    }
}
