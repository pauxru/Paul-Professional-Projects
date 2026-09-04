using Idp.Application.Metrics;

namespace Idp.UnitTests;

/// <summary>STP rate = auto-approved / documents that reached a routing decision.</summary>
public class StpMetricsTests
{
    [Fact]
    public void Straight_through_rate_is_auto_approved_over_processed()
    {
        var snapshot = new StpSnapshot(
            TotalDocuments: 19, Processed: 19, AutoApproved: 6, InReview: 10,
            Rejected: 3, Exported: 6, Failed: 0, ReviewQueueDepth: 13);
        Assert.Equal(6.0 / 19.0, snapshot.StraightThroughRate, 6);
    }

    [Fact]
    public void Straight_through_rate_is_zero_when_nothing_processed()
    {
        var snapshot = new StpSnapshot(0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0.0, snapshot.StraightThroughRate);
    }

    [Fact]
    public void Fully_automated_corpus_reports_full_rate()
    {
        var snapshot = new StpSnapshot(5, 5, 5, 0, 0, 5, 0, 0);
        Assert.Equal(1.0, snapshot.StraightThroughRate);
    }
}
