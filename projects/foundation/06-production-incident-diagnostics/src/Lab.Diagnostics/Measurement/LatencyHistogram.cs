namespace Lab.Diagnostics.Measurement;

public sealed class LatencyHistogram
{
    private readonly object _gate = new();
    private readonly List<double> _milliseconds = [];

    public void Record(TimeSpan latency)
    {
        if (latency < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(latency), "Latency cannot be negative.");
        }

        lock (_gate)
        {
            _milliseconds.Add(latency.TotalMilliseconds);
        }
    }

    public LatencySummary Snapshot()
    {
        double[] values;
        lock (_gate)
        {
            values = [.. _milliseconds];
        }

        if (values.Length == 0)
        {
            return LatencySummary.Empty;
        }

        Array.Sort(values);
        return new LatencySummary(
            values.Length,
            values[0],
            values[^1],
            values.Average(),
            Percentile(values, 0.50),
            Percentile(values, 0.95),
            Percentile(values, 0.99));
    }

    public static double Percentile(IReadOnlyList<double> sortedValues, double quantile)
    {
        if (quantile is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(quantile));
        }

        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var index = Math.Clamp((int)Math.Ceiling(sortedValues.Count * quantile) - 1, 0, sortedValues.Count - 1);
        return sortedValues[index];
    }
}

public sealed record LatencySummary(
    int Count,
    double MinMilliseconds,
    double MaxMilliseconds,
    double MeanMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds)
{
    public static LatencySummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}
