namespace LoadRunner.Core.Scenarios;

/// <summary>
/// Deterministic sampler for think-time distributions. Uniform, normal (Box–Muller) and
/// exponential are supported; constant returns the mean; None returns zero.
/// </summary>
public sealed class ThinkTimeSampler
{
    private readonly Random _rng;

    public ThinkTimeSampler(int seed) => _rng = new Random(seed);

    public TimeSpan Sample(ThinkTimeSpec spec)
    {
        var seconds = spec.Distribution switch
        {
            ThinkTimeDistribution.None => 0.0,
            ThinkTimeDistribution.Constant => spec.Mean,
            ThinkTimeDistribution.Uniform => SampleUniform(spec.Min, spec.Max),
            ThinkTimeDistribution.Normal => Math.Max(0, SampleNormal(spec.Mean, spec.StdDev)),
            ThinkTimeDistribution.Exponential => SampleExp(spec.Mean),
            _ => 0.0
        };
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        return TimeSpan.FromSeconds(seconds);
    }

    private double SampleUniform(double lo, double hi)
    {
        if (hi <= lo) return lo;
        return lo + _rng.NextDouble() * (hi - lo);
    }

    private double SampleNormal(double mean, double stddev)
    {
        // Box–Muller
        double u1;
        do { u1 = _rng.NextDouble(); } while (u1 <= double.Epsilon);
        var u2 = _rng.NextDouble();
        var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + z * stddev;
    }

    private double SampleExp(double mean)
    {
        if (mean <= 0) return 0;
        var u = _rng.NextDouble();
        // avoid Log(0)
        if (u <= double.Epsilon) u = double.Epsilon;
        return -mean * Math.Log(u);
    }
}
