using System.Diagnostics;

namespace Bridge.Report;

/// <summary>A measured result: the median and the spread, never a single number.</summary>
public readonly record struct Measurement(
    string Name, double MedianNs, double MinNs, double MaxNs, int Samples, long OpsPerSample)
{
    /// <summary>Relative spread of the samples, as a fraction of the median.</summary>
    public double Spread => MedianNs <= 0 ? 0 : (MaxNs - MinNs) / MedianNs;

    public string Format() => MedianNs switch
    {
        < 1.0 => $"{MedianNs:F3} ns",
        < 1000.0 => $"{MedianNs:F1} ns",
        < 1_000_000.0 => $"{MedianNs / 1000.0:F2} us",
        _ => $"{MedianNs / 1_000_000.0:F2} ms",
    };
}

/// <summary>
/// A deliberately small benchmark harness.
/// </summary>
/// <remarks>
/// <para>
/// BenchmarkDotNet would be the right tool for a library whose headline claim is a
/// number. It is the wrong tool here for two reasons: this project's claims are ratios
/// and orderings rather than absolute timings, and BenchmarkDotNet spawns per-benchmark
/// processes, which makes it impossible to observe the one effect that matters most --
/// what a native call does to a garbage collection happening on another thread in the
/// same process.
/// </para>
/// <para>
/// What this harness does take from that discipline: warm the JIT before measuring,
/// report a median over repeated samples rather than a mean over one, keep the
/// per-sample work large enough that the timer's resolution is irrelevant, and consume
/// results so the optimiser cannot delete the thing being measured.
/// </para>
/// </remarks>
public static class Bench
{
    /// <summary>Prevents dead-code elimination of a measured computation.</summary>
    public static double Sink;

    public static Measurement Measure(string name, long opsPerSample, Action<long> body,
                                      int samples = 9, int warmups = 3)
    {
        for (var i = 0; i < warmups; i++)
        {
            body(opsPerSample);
        }

        var results = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            var sw = Stopwatch.StartNew();
            body(opsPerSample);
            sw.Stop();
            results[i] = sw.Elapsed.TotalNanoseconds / opsPerSample;
        }

        Array.Sort(results);
        return new Measurement(name, results[samples / 2], results[0], results[^1],
                               samples, opsPerSample);
    }

    /// <summary>
    /// Rounds a ratio to the precision it deserves.
    /// </summary>
    /// <remarks>
    /// A measured speedup of 47.3182x is four digits of noise attached to one digit of
    /// signal. Reporting "47x" is not laziness; it is the honest precision of a
    /// wall-clock measurement taken nine times on a shared machine.
    /// </remarks>
    public static string Ratio(double numerator, double denominator)
    {
        if (denominator <= 0)
        {
            return "n/a";
        }
        var r = numerator / denominator;
        return r switch
        {
            >= 100 => $"{r:F0}x",
            >= 10 => $"{r:F0}x",
            >= 2 => $"{r:F1}x",
            _ => $"{r:F2}x",
        };
    }
}
