using FieldOps.Application;
using FieldOps.Domain;

namespace FieldOps.UnitTests;

public sealed class EntitlementQuotaTests
{
    [Fact]
    public void EnsureFeature_FreeInspection_RequiresUpgrade()
    {
        var service = new EntitlementService();
        var exception = Assert.Throws<EntitlementDeniedException>(
            () => service.EnsureFeature(SubscriptionPlan.Free, "inspections"));
        Assert.Equal("inspections", exception.Feature);
    }

    [Fact]
    public void EnsureFeature_ProfessionalFlags_IsAllowed()
    {
        new EntitlementService().EnsureFeature(SubscriptionPlan.Professional, "feature-flags");
    }

    [Fact]
    public void EnsureLimit_AtBoundary_IsAllowed()
    {
        new EntitlementService().EnsureLimit("assets", 9, 1, 10);
    }

    [Fact]
    public void EnsureLimit_AboveBoundary_ThrowsQuotaException()
    {
        var exception = Assert.Throws<QuotaExceededException>(
            () => new EntitlementService().EnsureLimit("assets", 10, 1, 10));
        Assert.False(exception.RateLimit);
        Assert.Equal(10, exception.Limit);
    }

    [Fact]
    public async Task IncrementMonthly_CrossesSoftLimit_ReturnsWarning()
    {
        var tenant = Guid.NewGuid();
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-03T00:00:00Z"));
        var service = new UsageQuotaService(new MemoryUsageStore(), clock);
        await service.IncrementMonthlyAsync(tenant, "jobs", 7, 8, 10, false, default);
        var result = await service.IncrementMonthlyAsync(tenant, "jobs", 1, 8, 10, false, default);
        Assert.True(result.SoftLimitReached);
        Assert.Equal(8, result.Value);
    }

    [Fact]
    public async Task IncrementMonthly_AboveHardLimit_DoesNotIncrement()
    {
        var tenant = Guid.NewGuid();
        var store = new MemoryUsageStore();
        var service = new UsageQuotaService(store, new FakeClock(DateTimeOffset.Parse("2026-09-03T00:00:00Z")));
        await service.IncrementMonthlyAsync(tenant, "jobs", 10, 8, 10, false, default);
        await Assert.ThrowsAsync<QuotaExceededException>(
            () => service.IncrementMonthlyAsync(tenant, "jobs", 1, 8, 10, false, default));
        Assert.Equal(10, await service.GetMonthlyAsync(tenant, "jobs", default));
    }

    [Fact]
    public async Task MonthlyPeriod_WhenClockRollsOver_ResetsUsageWindow()
    {
        var tenant = Guid.NewGuid();
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-30T23:59:00Z"));
        var service = new UsageQuotaService(new MemoryUsageStore(), clock);
        await service.IncrementMonthlyAsync(tenant, "jobs", 4, 8, 10, false, default);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(new DateOnly(2026, 10, 1), service.CurrentMonthlyPeriod);
        Assert.Equal(0, await service.GetMonthlyAsync(tenant, "jobs", default));
    }

    [Fact]
    public async Task ConcurrentIncrements_MemoryStore_RemainAtomic()
    {
        var tenant = Guid.NewGuid();
        var service = new UsageQuotaService(
            new MemoryUsageStore(),
            new FakeClock(DateTimeOffset.Parse("2026-09-03T00:00:00Z")));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            service.IncrementMonthlyAsync(tenant, "api", 1, 80, 100, true, default)));
        Assert.Equal(20, await service.GetMonthlyAsync(tenant, "api", default));
    }
}
