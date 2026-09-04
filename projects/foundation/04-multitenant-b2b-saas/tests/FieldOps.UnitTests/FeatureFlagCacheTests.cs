using FieldOps.Application;
using FieldOps.Domain;
using FieldOps.Infrastructure;

namespace FieldOps.UnitTests;

public sealed class FeatureFlagCacheTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-03T00:00:00Z");

    [Fact]
    public void StableBucket_SameKeyAndUser_IsDeterministic()
    {
        var user = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var first = FeatureFlagService.StableBucket("mobile-inspections", user);
        var second = FeatureFlagService.StableBucket("MOBILE-INSPECTIONS", user);
        Assert.Equal(first, second);
        Assert.InRange(first, 0, 99);
    }

    [Fact]
    public async Task Evaluate_ZeroPercent_IsDisabled()
    {
        var fixture = CreateFixture(new FeatureFlag(Guid.NewGuid(), "new-board", true, 0, false));
        var result = await fixture.Service.EvaluateAsync("new-board", Guid.NewGuid(), default);
        Assert.False(result.Enabled);
        Assert.StartsWith("rollout-bucket-", result.Reason);
    }

    [Fact]
    public async Task Evaluate_HundredPercent_IsEnabled()
    {
        var tenant = Guid.NewGuid();
        var fixture = CreateFixture(new FeatureFlag(tenant, "new-board", true, 100, false), tenant);
        var result = await fixture.Service.EvaluateAsync("new-board", Guid.NewGuid(), default);
        Assert.True(result.Enabled);
    }

    [Fact]
    public async Task Evaluate_KillSwitch_WinsOverUserOverride()
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var flag = new FeatureFlag(tenant, "new-board", true, 100, true);
        var fixture = CreateFixture(flag, tenant);
        fixture.Store.Overrides[(flag.Key, user)] = new FeatureFlagOverride(tenant, flag.Key, user, true);
        var result = await fixture.Service.EvaluateAsync(flag.Key, user, default);
        Assert.False(result.Enabled);
        Assert.Equal("kill-switch", result.Reason);
    }

    [Fact]
    public async Task Evaluate_UserOverride_WinsOverPercentage()
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var flag = new FeatureFlag(tenant, "new-board", true, 0, false);
        var fixture = CreateFixture(flag, tenant);
        fixture.Store.Overrides[(flag.Key, user)] = new FeatureFlagOverride(tenant, flag.Key, user, true);
        var result = await fixture.Service.EvaluateAsync(flag.Key, user, default);
        Assert.True(result.Enabled);
        Assert.Equal("user-override", result.Reason);
    }

    [Fact]
    public async Task Upsert_Change_IsAudited()
    {
        var tenant = Guid.NewGuid();
        var fixture = CreateFixture(new FeatureFlag(tenant, "new-board", true, 10, false), tenant);
        var changed = await fixture.Service.UpsertAsync("new-board", false, 0, true, "actor", "corr", default);
        Assert.True(changed.KillSwitch);
        Assert.Contains(fixture.Audit.Writes, x => x.Action == "feature-flag.changed");
    }

    [Fact]
    public async Task TenantCache_SameLogicalKey_IsInvisibleAcrossTenants()
    {
        var store = new TenantCacheStore();
        var clock = new FakeClock(Now);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var cacheA = new TenantAwareMemoryCache(new FakeTenantContext(tenantA), clock, store);
        var cacheB = new TenantAwareMemoryCache(new FakeTenantContext(tenantB), clock, store);
        await cacheA.SetAsync("dashboard", "tenant-a", TimeSpan.FromMinutes(1));
        Assert.Equal("tenant-a", await cacheA.GetAsync<string>("dashboard"));
        Assert.Null(await cacheB.GetAsync<string>("dashboard"));
    }

    [Fact]
    public void TenantCache_UnNamespacedPhysicalKey_IsRejected()
    {
        var tenant = Guid.NewGuid();
        var cache = new TenantAwareMemoryCache(
            new FakeTenantContext(tenant),
            new FakeClock(Now),
            new TenantCacheStore());
        Assert.Throws<CrossTenantAccessException>(() => cache.AssertNamespaced("dashboard"));
    }

    [Fact]
    public void TenantCache_CallerSuppliedPhysicalKey_IsRejected()
    {
        var cache = new TenantAwareMemoryCache(
            new FakeTenantContext(Guid.NewGuid()),
            new FakeClock(Now),
            new TenantCacheStore());
        Assert.Throws<CrossTenantAccessException>(() => cache.ToPhysicalKey("tenant:fake:key"));
    }

    [Fact]
    public async Task PermissionService_RoleChange_InvalidatesCacheImmediately()
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var membership = new Membership(tenant, user, MemberRole.Viewer, Now);
        var cache = new TenantAwareMemoryCache(
            new FakeTenantContext(tenant),
            new FakeClock(Now),
            new TenantCacheStore());
        var service = new PermissionService(
            new FakeTenantContext(tenant),
            new MemoryMembershipRepository(membership),
            cache);

        Assert.False(await service.HasPermissionAsync(user, Permissions.JobsCreate, default));
        await service.ChangeRoleAsync(membership.Id, MemberRole.Dispatcher, default);
        Assert.True(await service.HasPermissionAsync(user, Permissions.JobsCreate, default));
    }

    private static (FeatureFlagService Service, MemoryFlagStore Store, RecordingAuditWriter Audit) CreateFixture(
        FeatureFlag flag,
        Guid? tenantId = null)
    {
        var tenant = tenantId ?? flag.TenantId;
        var store = new MemoryFlagStore();
        store.Flags[flag.Key] = flag;
        var audit = new RecordingAuditWriter();
        var clock = new FakeClock(Now);
        var context = new FakeTenantContext(tenant);
        var cache = new TenantAwareMemoryCache(context, clock, new TenantCacheStore());
        return (new FeatureFlagService(context, store, cache, audit), store, audit);
    }
}
