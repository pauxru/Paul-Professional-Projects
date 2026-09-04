namespace LoadRunner.Core.Analysis;

/// <summary>
/// Ordinary-least-squares linear regression used by <see cref="SoakDriftDetector"/>. This
/// is intentionally re-implemented so tests can pin the numeric behaviour rather than
/// depend on a third-party stats library.
/// </summary>
public static class LinearRegression
{
    public sealed record Line(double Slope, double Intercept, double R2, double SlopePerMinute);

    public static Line Fit(IReadOnlyList<(double timeSec, double y)> points)
    {
        if (points.Count < 2) return new Line(0, points.Count == 0 ? 0 : points[0].y, 0, 0);
        var n = points.Count;
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
        foreach (var (x, y) in points)
        {
            sumX += x; sumY += y; sumXX += x * x; sumXY += x * y;
        }
        var meanX = sumX / n;
        var meanY = sumY / n;
        var denom = sumXX - n * meanX * meanX;
        var slope = denom == 0 ? 0 : (sumXY - n * meanX * meanY) / denom;
        var intercept = meanY - slope * meanX;
        double ssRes = 0, ssTot = 0;
        foreach (var (x, y) in points)
        {
            var pred = slope * x + intercept;
            ssRes += (y - pred) * (y - pred);
            ssTot += (y - meanY) * (y - meanY);
        }
        var r2 = ssTot == 0 ? 1 : 1 - (ssRes / ssTot);
        return new Line(slope, intercept, r2, slope * 60);
    }
}
