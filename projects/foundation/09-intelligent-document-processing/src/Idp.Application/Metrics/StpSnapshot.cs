using Idp.Domain.Documents;

namespace Idp.Application.Metrics;

/// <summary>Straight-through-processing metrics over the processed corpus.</summary>
public sealed record StpSnapshot(
    int TotalDocuments,
    int Processed,
    int AutoApproved,
    int InReview,
    int Rejected,
    int Exported,
    int Failed,
    int ReviewQueueDepth)
{
    /// <summary>STP rate = auto-approved documents / documents that reached a routing decision.</summary>
    public double StraightThroughRate =>
        Processed == 0 ? 0.0 : (double)AutoApproved / Processed;
}

/// <summary>Computes the STP snapshot and per-stage counts from the document corpus.</summary>
public interface IStpMetricsService
{
    Task<StpSnapshot> ComputeAsync(CancellationToken ct = default);
}
