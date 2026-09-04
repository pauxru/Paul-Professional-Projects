using FieldOps.Application;
using FieldOps.Domain;
using FieldOps.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Api;

public static class DemoData
{
    public static readonly Guid SavannaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid JuaKaliId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid AcmeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid OwnerUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid TechnicianUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid ViewerUserId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    public static readonly Guid PlatformAdminUserId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    public const string PlatformAdminEmail = "platform@fieldops.demo";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
            await db.Database.EnsureCreatedAsync(cancellationToken);
            if (!await db.Organizations.AnyAsync(cancellationToken))
            {
                var now = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
                db.Organizations.AddRange(
                    new Organization(SavannaId, "savanna-logistics", "Savanna Logistics Ltd (fictional)", "KE", SubscriptionPlan.Professional, now),
                    new Organization(JuaKaliId, "jua-kali-manufacturing", "Jua Kali Manufacturing Ltd (fictional)", "KE", SubscriptionPlan.Starter, now),
                    new Organization(AcmeId, "acme-manufacturing", "Acme Manufacturing (fictional)", "US", SubscriptionPlan.Enterprise, now));
                db.Users.AddRange(
                    new AppUser(OwnerUserId, "owner@fieldops.demo", "Demo Owner", now),
                    new AppUser(TechnicianUserId, "technician@fieldops.demo", "Demo Technician", now),
                    new AppUser(ViewerUserId, "viewer@fieldops.demo", "Demo Viewer", now),
                    new AppUser(PlatformAdminUserId, PlatformAdminEmail, "Platform Administrator", now));
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        await SeedTenantAsync(services, SavannaId, "savanna-logistics", SubscriptionPlan.Professional, cancellationToken);
        await SeedTenantAsync(services, JuaKaliId, "jua-kali-manufacturing", SubscriptionPlan.Starter, cancellationToken);
        await SeedTenantAsync(services, AcmeId, "acme-manufacturing", SubscriptionPlan.Enterprise, cancellationToken);
    }

    private static async Task SeedTenantAsync(
        IServiceProvider services,
        Guid tenantId,
        string slug,
        SubscriptionPlan plan,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IMutableTenantContext>();
        context.Set(tenantId, slug);
        var db = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        if (await db.Memberships.AnyAsync(cancellationToken)) return;

        var now = DateTimeOffset.Parse("2026-09-01T08:00:00Z");
        db.Memberships.AddRange(
            new Membership(tenantId, OwnerUserId, MemberRole.Owner, now),
            new Membership(tenantId, TechnicianUserId, MemberRole.Technician, now),
            new Membership(tenantId, ViewerUserId, MemberRole.Viewer, now),
            new Membership(tenantId, PlatformAdminUserId, MemberRole.Owner, now));
        var asset = new Asset(tenantId, $"EQ-{slug[..3].ToUpperInvariant()}-001", "Hydraulic Lift", "Workshop Equipment", "Nairobi", new DateOnly(2026, 10, 15));
        db.Assets.Add(asset);
        db.Jobs.Add(new Job(
            tenantId,
            "Quarterly equipment service",
            "Inspect and service the fictional demonstration asset.",
            JobPriority.High,
            now.AddDays(1),
            now.AddDays(1).AddHours(4),
            now.AddDays(1).AddHours(6),
            now,
            asset.Id));
        db.FeatureFlags.Add(new FeatureFlag(tenantId, "mobile-inspections", plan != SubscriptionPlan.Free, 50, false));
        await db.SaveChangesAsync(cancellationToken);
    }
}
