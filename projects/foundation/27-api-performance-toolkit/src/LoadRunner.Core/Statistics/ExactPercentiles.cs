namespace LoadRunner.Core.Statistics;

/// <summary>
/// Exact-percentile calculator for small runs: retains every sample up to a cap and
/// computes percentiles by nearest-rank on the sorted set.
/// </summary>
public sealed class ExactPercentiles
{
    private readonly List<double> _samples;
    public int MaxSamples { get; }
    public int Count => _samples.Count;
    public bool Overflowed { get; private set; }

    public ExactPercentiles(int maxSamples = 100_000)
    {
        MaxSamples = maxSamples;
        _samples = new List<double>(Math.Min(maxSamples, 1024));
    }

    public void Add(double value)
    {
        if (_samples.Count >= MaxSamples) { Overflowed = true; return; }
        _samples.Add(value);
    }

    public double Percentile(double p)
    {
        if (_samples.Count == 0) return 0;
        p = Math.Clamp(p, 0.0, 100.0);
        var sorted = _samples.ToArray();
        Array.Sort(sorted);
        // Nearest-rank: rank = ceil(p/100 * n), then value at rank-1
        var rank = (int)Math.Ceiling((p / 100.0) * sorted.Length);
        if (rank <= 0) rank = 1;
        if (rank > sorted.Length) rank = sorted.Length;
        return sorted[rank - 1];
    }

    public double Mean() => _samples.Count == 0 ? 0 : _samples.Average();
    public double Min() => _samples.Count == 0 ? 0 : _samples.Min();
    public double Max() => _samples.Count == 0 ? 0 : _samples.Max();

    public double StdDev()
    {
        if (_samples.Count == 0) return 0;
        var mean = Mean();
        var sumSq = 0.0;
        foreach (var v in _samples) sumSq += (v - mean) * (v - mean);
        return Math.Sqrt(sumSq / _samples.Count);
    }

    public IReadOnlyList<double> Samples => _samples;
}
