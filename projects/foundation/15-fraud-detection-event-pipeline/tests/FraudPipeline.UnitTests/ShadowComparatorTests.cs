using FraudPipeline.Application.Shadow;
using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.ValueObjects;
using FraudPipeline.UnitTests.Fakes;

namespace FraudPipeline.UnitTests;

public class ShadowComparatorTests
{
    private static readonly DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ScoringDecision Dec(string txnRef, Decision d, bool shadow)
        => new(
            id: Guid.NewGuid(),
            transactionId: Guid.NewGuid(),
            transactionRef: txnRef,
            rulesetVersion: shadow ? "v-shadow" : "v-live",
            score: 500,
            decision: d,
            reasons: "",
            rulesFiredJson: "[]",
            featureVectorJson: "{}",
            latencyMs: 1.0,
            budgetExceeded: false,
            shadow: shadow,
            decidedAt: _now);

    [Fact]
    public async Task CompareAsync_AllMatch_ReturnsMatchRateOne()
    {
        var repo = new InMemoryScoringDecisionRepository();
        for (int i = 0; i < 5; i++)
        {
            await repo.AddAsync(Dec($"TX-{i:D3}", Decision.Approve, shadow: false));
            await repo.AddAsync(Dec($"TX-{i:D3}", Decision.Approve, shadow: true));
        }

        var cmp = new ShadowComparator(repo);
        var result = await cmp.CompareAsync(100);

        Assert.Equal(5, result.TotalPairs);
        Assert.Equal(5, result.Matches);
        Assert.Equal(0, result.Differences);
        Assert.Equal(1.0, result.MatchRate);
    }

    [Fact]
    public async Task CompareAsync_DifferencesCounted_ByTransitionKey()
    {
        var repo = new InMemoryScoringDecisionRepository();
        await repo.AddAsync(Dec("TX-1", Decision.Approve, shadow: false));
        await repo.AddAsync(Dec("TX-1", Decision.Review, shadow: true));
        await repo.AddAsync(Dec("TX-2", Decision.Review, shadow: false));
        await repo.AddAsync(Dec("TX-2", Decision.Decline, shadow: true));
        await repo.AddAsync(Dec("TX-3", Decision.Approve, shadow: false));
        await repo.AddAsync(Dec("TX-3", Decision.Approve, shadow: true));

        var cmp = new ShadowComparator(repo);
        var result = await cmp.CompareAsync(100);

        Assert.Equal(3, result.TotalPairs);
        Assert.Equal(1, result.Matches);
        Assert.Equal(2, result.Differences);
        Assert.True(result.DifferenceByDecision.ContainsKey("Approve->Review"));
        Assert.True(result.DifferenceByDecision.ContainsKey("Review->Decline"));
    }
}
