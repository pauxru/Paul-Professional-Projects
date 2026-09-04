namespace LoadRunner.Core.Analysis;

/// <summary>
/// Binary search for the maximum sustainable arrival rate that keeps p95 latency below
/// a target and error rate below an acceptable ceiling. The caller supplies a probe
/// delegate that runs the workload at a given rate and returns the resulting p95 + error
/// rate; the search converges in <c>ceil(log2((max-min)/tolerance))</c> probes.
/// </summary>
public sealed class CapacityBinarySearch
{
    public sealed record ProbeOutcome(double P95Ms, double ErrorRate);

    public sealed record CapacityResult(
        int MaximumSustainedRate,
        double AchievedP95Ms,
        double AchievedErrorRate,
        int ProbesRun,
        IReadOnlyList<(int rate, double p95, double err)> Trace);

    public async Task<CapacityResult> SearchAsync(
        int minRate,
        int maxRate,
        int tolerance,
        double p95TargetMs,
        double errorRateCeiling,
        Func<int, CancellationToken, Task<ProbeOutcome>> probe,
        CancellationToken cancellationToken = default)
    {
        if (minRate <= 0) throw new ArgumentOutOfRangeException(nameof(minRate));
        if (maxRate <= minRate) throw new ArgumentOutOfRangeException(nameof(maxRate));
        if (tolerance <= 0) throw new ArgumentOutOfRangeException(nameof(tolerance));

        var trace = new List<(int rate, double p95, double err)>();
        var bestRate = 0;
        double bestP95 = 0, bestErr = 0;
        var probes = 0;

        var lo = minRate;
        var hi = maxRate;
        while (hi - lo > tolerance)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mid = lo + (hi - lo) / 2;
            var outcome = await probe(mid, cancellationToken).ConfigureAwait(false);
            probes++;
            trace.Add((mid, outcome.P95Ms, outcome.ErrorRate));
            if (outcome.P95Ms <= p95TargetMs && outcome.ErrorRate <= errorRateCeiling)
            {
                bestRate = mid;
                bestP95 = outcome.P95Ms;
                bestErr = outcome.ErrorRate;
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        return new CapacityResult(bestRate, bestP95, bestErr, probes, trace);
    }
}
