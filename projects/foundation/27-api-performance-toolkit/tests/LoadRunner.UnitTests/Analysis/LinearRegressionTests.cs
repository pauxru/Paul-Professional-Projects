using LoadRunner.Core.Analysis;
using Xunit;

namespace LoadRunner.UnitTests.Analysis;

public class LinearRegressionTests
{
    [Fact]
    public void FitsAKnownLine_Exactly()
    {
        // y = 3x + 4
        var pts = new List<(double, double)>();
        for (var x = 0; x <= 10; x++) pts.Add((x, 3 * x + 4));
        var line = LinearRegression.Fit(pts);
        Assert.Equal(3, line.Slope, 6);
        Assert.Equal(4, line.Intercept, 6);
        Assert.Equal(1.0, line.R2, 6);
        Assert.Equal(3 * 60, line.SlopePerMinute, 6);
    }

    [Fact]
    public void HandlesConstantSeries_WithoutError()
    {
        var pts = Enumerable.Range(0, 10).Select(i => ((double)i, 5.0)).ToArray();
        var line = LinearRegression.Fit(pts);
        Assert.Equal(0, line.Slope, 6);
        Assert.Equal(5, line.Intercept, 6);
    }
}
