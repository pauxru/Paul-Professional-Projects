namespace LoadRunner.Core.Statistics;

/// <summary>
/// Statistical comparison between two independent samples. Two methods are offered:
///
///   * Mann–Whitney U test — a non-parametric rank test for a location shift with no
///     assumption of normality. We use the normal approximation with tie correction which
///     is standard for n >= 20 and behaves well for typical latency distributions.
///   * Bootstrap confidence interval on the difference of medians. Deterministic given a
///     seed so the same inputs produce the same verdict, which matters for CI gating.
/// </summary>
public static class SignificanceTest
{
    public enum Verdict { NoSignificantChange, Improved, Regressed }

    public sealed record MannWhitneyResult(
        double U1,
        double U2,
        double ZScore,
        double PValue,
        Verdict Verdict);

    public sealed record BootstrapResult(
        double MedianA,
        double MedianB,
        double MedianDelta,
        double CiLow,
        double CiHigh,
        double Confidence,
        Verdict Verdict);

    /// <summary>
    /// Mann–Whitney U with a normal approximation and tie correction.
    ///   H0: distributions are identical.
    ///   H1 (two-sided): they differ.
    /// The Verdict is set to Regressed if B is stochastically larger than A (b tends to be
    /// slower — bad, when the values are latencies) and Improved if the opposite.
    /// </summary>
    public static MannWhitneyResult MannWhitneyU(double[] a, double[] b, double alpha = 0.05)
    {
        if (a.Length == 0 || b.Length == 0)
            return new MannWhitneyResult(0, 0, 0, 1, Verdict.NoSignificantChange);

        var n1 = a.Length;
        var n2 = b.Length;
        var combined = new (double value, int group)[n1 + n2];
        for (var i = 0; i < n1; i++) combined[i] = (a[i], 1);
        for (var i = 0; i < n2; i++) combined[n1 + i] = (b[i], 2);
        Array.Sort(combined, (x, y) => x.value.CompareTo(y.value));

        // Rank assignment with average of tied ranks
        var ranks = new double[combined.Length];
        var tieSum = 0.0;
        var idx = 0;
        while (idx < combined.Length)
        {
            var end = idx;
            while (end + 1 < combined.Length && combined[end + 1].value == combined[idx].value) end++;
            var avg = ((idx + 1) + (end + 1)) / 2.0;
            for (var k = idx; k <= end; k++) ranks[k] = avg;
            var t = end - idx + 1;
            if (t > 1) tieSum += (t * t * t - t);
            idx = end + 1;
        }

        var r1 = 0.0;
        for (var i = 0; i < combined.Length; i++) if (combined[i].group == 1) r1 += ranks[i];

        var u1 = r1 - (n1 * (n1 + 1) / 2.0);
        var u2 = (double)n1 * n2 - u1;
        var meanU = n1 * n2 / 2.0;
        var n = (double)(n1 + n2);
        var varUCorrected = ((double)n1 * n2 / 12.0) * ((n + 1) - tieSum / (n * (n - 1)));
        var sd = Math.Sqrt(Math.Max(varUCorrected, 1e-12));
        // continuity correction
        var u = Math.Min(u1, u2);
        var z = (u - meanU + 0.5) / sd;
        var pTwoSided = 2 * NormalCdf(-Math.Abs(z));
        var verdict = Verdict.NoSignificantChange;
        if (pTwoSided < alpha)
        {
            // If a's median < b's median => b regressed
            var medA = Median(a);
            var medB = Median(b);
            verdict = medB > medA ? Verdict.Regressed : Verdict.Improved;
        }
        return new MannWhitneyResult(u1, u2, z, pTwoSided, verdict);
    }

    /// <summary>
    /// Deterministic bootstrap of the difference of medians (B minus A) with a percentile
    /// confidence interval. Positive delta => B has a larger median (worse for latency).
    /// </summary>
    public static BootstrapResult BootstrapMedianDiff(
        double[] a, double[] b,
        int iterations = 2000,
        double confidence = 0.95,
        int seed = 42)
    {
        if (a.Length == 0 || b.Length == 0)
            return new BootstrapResult(0, 0, 0, 0, 0, confidence, Verdict.NoSignificantChange);

        var rng = new Random(seed);
        var deltas = new double[iterations];
        var sampleA = new double[a.Length];
        var sampleB = new double[b.Length];
        for (var it = 0; it < iterations; it++)
        {
            for (var i = 0; i < a.Length; i++) sampleA[i] = a[rng.Next(a.Length)];
            for (var i = 0; i < b.Length; i++) sampleB[i] = b[rng.Next(b.Length)];
            deltas[it] = Median(sampleB) - Median(sampleA);
        }
        Array.Sort(deltas);
        var lowIdx = (int)Math.Floor(((1 - confidence) / 2.0) * iterations);
        var highIdx = (int)Math.Ceiling((1 - (1 - confidence) / 2.0) * iterations) - 1;
        lowIdx = Math.Clamp(lowIdx, 0, iterations - 1);
        highIdx = Math.Clamp(highIdx, 0, iterations - 1);
        var low = deltas[lowIdx];
        var high = deltas[highIdx];
        var medA = Median(a);
        var medB = Median(b);
        var delta = medB - medA;

        // Verdict: interval strictly above zero => regressed (b slower)
        //          interval strictly below zero => improved (b faster)
        //          interval brackets zero => no significant change
        var verdict = Verdict.NoSignificantChange;
        if (low > 0) verdict = Verdict.Regressed;
        else if (high < 0) verdict = Verdict.Improved;
        return new BootstrapResult(medA, medB, delta, low, high, confidence, verdict);
    }

    public static double Median(double[] values)
    {
        if (values.Length == 0) return 0;
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var n = sorted.Length;
        if (n % 2 == 1) return sorted[n / 2];
        return (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    /// <summary>Standard normal CDF via an Abramowitz &amp; Stegun approximation.</summary>
    public static double NormalCdf(double z)
    {
        // 7.1.26 series, error ~ 1.5e-7
        var b1 =  0.319381530;
        var b2 = -0.356563782;
        var b3 =  1.781477937;
        var b4 = -1.821255978;
        var b5 =  1.330274429;
        var p  =  0.2316419;
        var t = 1.0 / (1.0 + p * Math.Abs(z));
        var pdf = Math.Exp(-0.5 * z * z) / Math.Sqrt(2 * Math.PI);
        var cdf = 1.0 - pdf * (b1 * t + b2 * t * t + b3 * Math.Pow(t, 3) + b4 * Math.Pow(t, 4) + b5 * Math.Pow(t, 5));
        return z >= 0 ? cdf : 1 - cdf;
    }
}
