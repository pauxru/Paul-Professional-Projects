using LoadRunner.Core.Scenarios;
using Xunit;

namespace LoadRunner.UnitTests.Scenarios;

public class CsvFeederTests
{
    [Fact]
    public void FromLines_ReadsHeaderAndRowsCorrectly()
    {
        var feeder = CsvFeeder.FromLines(new[]
        {
            "id,name,price",
            "1,Coffee,4.50",
            "2,\"Tea, Ceylon\",3.20",
            "3,Cocoa,2.10"
        });
        var first = feeder.Next()!;
        Assert.Equal("1", first["id"]);
        Assert.Equal("Coffee", first["name"]);
        var second = feeder.Next()!;
        Assert.Equal("Tea, Ceylon", second["name"]);
    }

    [Fact]
    public void CyclingFeeder_WrapsAroundAtEnd()
    {
        var feeder = CsvFeeder.FromLines(new[] { "x", "a", "b" }, cycle: true);
        Assert.Equal("a", feeder.Next()!["x"]);
        Assert.Equal("b", feeder.Next()!["x"]);
        Assert.Equal("a", feeder.Next()!["x"]);
        Assert.Equal("b", feeder.Next()!["x"]);
    }

    [Fact]
    public void NonCyclingFeeder_ReturnsNullAtEnd()
    {
        var feeder = CsvFeeder.FromLines(new[] { "x", "a", "b" }, cycle: false);
        Assert.NotNull(feeder.Next());
        Assert.NotNull(feeder.Next());
        Assert.Null(feeder.Next());
    }
}
