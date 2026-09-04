namespace NotificationPlatform.UnitTests;

using NotificationPlatform.Application.Fairness;
using Xunit;

public sealed class FairnessSchedulerTests
{
    private static TenantBacklog Backlog(Guid tenant, double weight, int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        return new TenantBacklog(tenant, weight, ids);
    }

    [Fact]
    public void NoTenantIsStarvedUnderEqualWeights()
    {
        var s = new TenantFairnessScheduler();
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var backlogs = new[]
        {
            Backlog(a, 1.0, 1000),
            Backlog(b, 1.0, 1000),
            Backlog(c, 1.0, 1000),
        };
        var batch = s.PickBatch(backlogs, 30);
        var byTenant = backlogs.ToDictionary(bl => bl.TenantId, bl => new HashSet<Guid>(bl.PendingIds));
        int aCount = batch.Count(id => byTenant[a].Contains(id));
        int bCount = batch.Count(id => byTenant[b].Contains(id));
        int cCount = batch.Count(id => byTenant[c].Contains(id));
        Assert.True(aCount >= 5, $"A only got {aCount}");
        Assert.True(bCount >= 5, $"B only got {bCount}");
        Assert.True(cCount >= 5, $"C only got {cCount}");
    }

    [Fact]
    public void HeavierWeightGetsMoreSlots()
    {
        var s = new TenantFairnessScheduler();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var backlogs = new[]
        {
            Backlog(a, 3.0, 1000),
            Backlog(b, 1.0, 1000),
        };
        var batch = s.PickBatch(backlogs, 400);
        var aIds = backlogs[0].PendingIds.ToHashSet();
        var bIds = backlogs[1].PendingIds.ToHashSet();
        int aCount = batch.Count(id => aIds.Contains(id));
        int bCount = batch.Count(id => bIds.Contains(id));
        Assert.True(aCount > bCount * 2, $"A={aCount}, B={bCount}");
    }

    [Fact]
    public void EmptyBacklogsReturnEmpty()
    {
        var s = new TenantFairnessScheduler();
        Assert.Empty(s.PickBatch(Array.Empty<TenantBacklog>(), 10));
    }
}
