using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubscriptionBilling.Application;
using SubscriptionBilling.Domain;
using SubscriptionBilling.Infrastructure;

namespace SubscriptionBilling.Api;

public static class SeedData
{
    public static readonly Guid ApiCallsMeterId =
        Guid.Parse("16000000-0000-0000-0000-000000000001");
    public static readonly Guid ActiveSeatsMeterId =
        Guid.Parse("16000000-0000-0000-0000-000000000002");
    public static readonly Guid StoragePeakMeterId =
        Guid.Parse("16000000-0000-0000-0000-000000000003");
    public static readonly Guid UniqueDevicesMeterId =
        Guid.Parse("16000000-0000-0000-0000-000000000004");
    public static readonly Guid ProductId =
        Guid.Parse("16000000-0000-0000-0000-000000000010");
    public static readonly Guid GrowthPlanId =
        Guid.Parse("16000000-0000-0000-0000-000000000020");
    public static readonly Guid GrowthPlanVersionId =
        Guid.Parse("16000000-0000-0000-0000-000000000021");
    public static readonly Guid MeteredPlanId =
        Guid.Parse("16000000-0000-0000-0000-000000000030");
    public static readonly Guid MeteredPlanVersionId =
        Guid.Parse("16000000-0000-0000-0000-000000000031");
    public static readonly Guid ContosoCustomerId =
        Guid.Parse("16000000-0000-0000-0000-000000000040");
    public static readonly Guid SavannaCustomerId =
        Guid.Parse("16000000-0000-0000-0000-000000000041");
    public static readonly Guid LaunchCouponId =
        Guid.Parse("16000000-0000-0000-0000-000000000050");

    public static async Task InitializeAsync(
        BillingDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!await db.Meters.AnyAsync(cancellationToken))
        {
            db.Meters.AddRange(
                Meter(ApiCallsMeterId, "API calls", "request", UsageAggregationMode.Sum),
                Meter(ActiveSeatsMeterId, "Active seats", "seat", UsageAggregationMode.LastValue),
                Meter(StoragePeakMeterId, "Peak storage", "GB", UsageAggregationMode.Max),
                Meter(UniqueDevicesMeterId, "Unique devices", "device", UsageAggregationMode.UniqueCount));
        }

        if (!await db.Products.AnyAsync(item => item.Id == ProductId, cancellationToken))
        {
            db.Products.Add(new ProductEntity
            {
                Id = ProductId,
                Name = "Billing Platform",
                Description = "Fictional reusable SaaS billing platform product.",
                IsActive = true,
                CreatedAt = clock.UtcNow
            });
        }

        if (!await db.Plans.AnyAsync(item => item.Id == GrowthPlanId, cancellationToken))
        {
            db.Plans.Add(new PlanEntity
            {
                Id = GrowthPlanId,
                ProductId = ProductId,
                Name = "Growth Monthly",
                IntervalUnit = BillingIntervalUnit.Month,
                IntervalCount = 1,
                CreatedAt = clock.UtcNow
            });
            db.PlanVersions.Add(new PlanVersionEntity
            {
                Id = GrowthPlanVersionId,
                PlanId = GrowthPlanId,
                Version = 1,
                EffectiveFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Currency = "KES",
                PricingJson = JsonSerializer.Serialize(new PricingConfiguration(
                    PricingModel.FlatRecurring,
                    FlatFeeMinor: 150_000)),
                TaxInclusive = false,
                CreatedAt = clock.UtcNow
            });
        }

        if (!await db.Plans.AnyAsync(item => item.Id == MeteredPlanId, cancellationToken))
        {
            db.Plans.Add(new PlanEntity
            {
                Id = MeteredPlanId,
                ProductId = ProductId,
                Name = "API Scale",
                IntervalUnit = BillingIntervalUnit.Month,
                IntervalCount = 1,
                MeterId = ApiCallsMeterId,
                CreatedAt = clock.UtcNow
            });
            db.PlanVersions.Add(new PlanVersionEntity
            {
                Id = MeteredPlanVersionId,
                PlanId = MeteredPlanId,
                Version = 1,
                EffectiveFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Currency = "USD",
                PricingJson = JsonSerializer.Serialize(new PricingConfiguration(
                    PricingModel.GraduatedWithOverage,
                    FlatFeeMinor: 5_000,
                    UnitPriceMinor: 2,
                    IncludedUnits: 10_000)),
                TaxInclusive = false,
                CreatedAt = clock.UtcNow
            });
        }

        if (!await db.Customers.AnyAsync(item => item.Id == ContosoCustomerId, cancellationToken))
        {
            db.Customers.AddRange(
                new CustomerEntity
                {
                    Id = ContosoCustomerId,
                    Name = "Contoso Retail",
                    Currency = "KES",
                    CountryCode = "KE",
                    CreatedAt = clock.UtcNow
                },
                new CustomerEntity
                {
                    Id = SavannaCustomerId,
                    Name = "Savanna Logistics Ltd (fictional)",
                    Currency = "USD",
                    CountryCode = "US",
                    CreatedAt = clock.UtcNow
                });
        }

        if (!await db.Coupons.AnyAsync(item => item.Id == LaunchCouponId, cancellationToken))
        {
            db.Coupons.Add(new CouponEntity
            {
                Id = LaunchCouponId,
                Code = "LAUNCH10",
                Type = CouponType.Percentage,
                Percentage = 10m,
                Duration = CouponDuration.Repeating,
                DurationCycles = 3,
                MaxRedemptions = 100,
                CreatedAt = clock.UtcNow
            });
        }

        if (!await db.Sequences.AnyAsync(item => item.Name == "invoice", cancellationToken))
        {
            db.Sequences.Add(new SequenceEntity { Name = "invoice", NextValue = 1 });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static MeterEntity Meter(
        Guid id,
        string name,
        string unit,
        UsageAggregationMode aggregation) =>
        new()
        {
            Id = id,
            Name = name,
            Unit = unit,
            Aggregation = aggregation,
            RoundingIncrement = 1m,
            RoundingMode = UsageRoundingMode.Up
        };
}
