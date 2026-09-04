namespace SubscriptionBilling.Domain;

public enum PricingModel
{
    FlatRecurring,
    PerUnit,
    Tiered,
    Volume,
    GraduatedWithOverage,
    Package,
    OneOff
}

public readonly record struct PriceTier(long? UpToInclusive, Money UnitPrice);

public readonly record struct PriceTierDefinition(long? UpToInclusive, long UnitPriceMinor);

public sealed record PricingConfiguration(
    PricingModel Model,
    long? FlatFeeMinor = null,
    long? UnitPriceMinor = null,
    long? IncludedUnits = null,
    long? BlockSize = null,
    long? BlockPriceMinor = null,
    long? OneOffAmountMinor = null,
    IReadOnlyList<PriceTierDefinition>? Tiers = null);

public interface IPricingStrategy
{
    PricingModel Model { get; }
    string Currency { get; }
    Money Calculate(long units);
}

public abstract class PricingStrategyBase(string currency) : IPricingStrategy
{
    public abstract PricingModel Model { get; }
    public string Currency { get; } = Money.Zero(currency).Currency;

    public abstract Money Calculate(long units);

    protected static void EnsureUnits(long units)
    {
        if (units < 0)
        {
            throw new DomainException("Billable units cannot be negative; use adjustment charges for credits.");
        }
    }
}

public sealed class FlatRecurringPricing(Money fee) : PricingStrategyBase(fee.Currency)
{
    public override PricingModel Model => PricingModel.FlatRecurring;
    public override Money Calculate(long units)
    {
        EnsureUnits(units);
        return fee;
    }
}

public sealed class PerUnitPricing(Money unitPrice) : PricingStrategyBase(unitPrice.Currency)
{
    public override PricingModel Model => PricingModel.PerUnit;
    public override Money Calculate(long units)
    {
        EnsureUnits(units);
        return unitPrice.Multiply(units);
    }
}

public sealed class TieredPricing : PricingStrategyBase
{
    private readonly IReadOnlyList<PriceTier> _tiers;

    public TieredPricing(IEnumerable<PriceTier> tiers)
        : base(ValidateAndGetCurrency(tiers, out var materialized))
    {
        _tiers = materialized;
    }

    public override PricingModel Model => PricingModel.Tiered;

    public override Money Calculate(long units)
    {
        EnsureUnits(units);
        var total = Money.Zero(Currency);
        var remaining = units;
        long previousCap = 0;

        foreach (var tier in _tiers)
        {
            if (remaining == 0)
            {
                break;
            }

            var capacity = tier.UpToInclusive is null
                ? remaining
                : checked(tier.UpToInclusive.Value - previousCap);
            var unitsInTier = Math.Min(remaining, capacity);
            total += tier.UnitPrice.Multiply(unitsInTier);
            remaining -= unitsInTier;
            if (tier.UpToInclusive is not null)
            {
                previousCap = tier.UpToInclusive.Value;
            }
        }

        if (remaining > 0)
        {
            throw new DomainException("Tiered pricing requires an open-ended top tier.");
        }

        return total;
    }

    private static string ValidateAndGetCurrency(
        IEnumerable<PriceTier> tiers,
        out IReadOnlyList<PriceTier> materialized)
    {
        var list = tiers?.ToList() ?? throw new ArgumentNullException(nameof(tiers));
        if (list.Count == 0)
        {
            throw new DomainException("At least one tier is required.");
        }

        var currency = list[0].UnitPrice.Currency;
        long previous = 0;
        for (var index = 0; index < list.Count; index++)
        {
            var tier = list[index];
            if (tier.UnitPrice.MinorUnits < 0)
            {
                throw new DomainException("Tier unit prices cannot be negative.");
            }

            if (!string.Equals(currency, tier.UnitPrice.Currency, StringComparison.Ordinal))
            {
                throw new DomainException("All tiers must use the same currency.");
            }

            if (tier.UpToInclusive is null)
            {
                if (index != list.Count - 1)
                {
                    throw new DomainException("Only the final tier may be open-ended.");
                }
            }
            else
            {
                if (tier.UpToInclusive <= previous)
                {
                    throw new DomainException("Tier caps must be strictly increasing.");
                }

                previous = tier.UpToInclusive.Value;
            }
        }

        materialized = list;
        return currency;
    }
}

public sealed class VolumePricing : PricingStrategyBase
{
    private readonly IReadOnlyList<PriceTier> _tiers;

    public VolumePricing(IEnumerable<PriceTier> tiers)
        : base(ValidateAndGetCurrency(tiers, out var materialized))
    {
        _tiers = materialized;
    }

    public override PricingModel Model => PricingModel.Volume;

    public override Money Calculate(long units)
    {
        EnsureUnits(units);
        var tier = _tiers.FirstOrDefault(candidate =>
            candidate.UpToInclusive is null || units <= candidate.UpToInclusive.Value);
        if (tier == default && units > 0)
        {
            throw new DomainException("Volume pricing requires an open-ended top tier.");
        }

        return units == 0 ? Money.Zero(Currency) : tier.UnitPrice.Multiply(units);
    }

    private static string ValidateAndGetCurrency(
        IEnumerable<PriceTier> tiers,
        out IReadOnlyList<PriceTier> materialized)
    {
        materialized = tiers?.ToList() ?? throw new ArgumentNullException(nameof(tiers));
        var validated = new TieredPricing(materialized);
        return validated.Currency;
    }
}

public sealed class GraduatedOveragePricing(
    Money baseFee,
    long includedUnits,
    Money overageUnitPrice) : PricingStrategyBase(baseFee.Currency)
{
    public override PricingModel Model => PricingModel.GraduatedWithOverage;

    public override Money Calculate(long units)
    {
        EnsureUnits(units);
        if (includedUnits < 0)
        {
            throw new DomainException("Included units cannot be negative.");
        }

        if (!string.Equals(baseFee.Currency, overageUnitPrice.Currency, StringComparison.Ordinal))
        {
            throw new DomainException("Base and overage prices must use the same currency.");
        }

        var overage = Math.Max(0, units - includedUnits);
        return baseFee + overageUnitPrice.Multiply(overage);
    }
}

public sealed class PackagePricing(
    long blockSize,
    Money blockPrice) : PricingStrategyBase(blockPrice.Currency)
{
    public override PricingModel Model => PricingModel.Package;

    public override Money Calculate(long units)
    {
        EnsureUnits(units);
        if (blockSize <= 0)
        {
            throw new DomainException("Block size must be positive.");
        }

        var blocks = units / blockSize + (units % blockSize == 0 ? 0 : 1);
        return blockPrice.Multiply(blocks);
    }
}

public sealed class OneOffPricing(Money charge) : PricingStrategyBase(charge.Currency)
{
    public override PricingModel Model => PricingModel.OneOff;
    public override Money Calculate(long units)
    {
        EnsureUnits(units);
        return charge;
    }
}

public static class PricingStrategyFactory
{
    public static IPricingStrategy Create(PricingConfiguration configuration, string currency)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.Model switch
        {
            PricingModel.FlatRecurring => new FlatRecurringPricing(
                new Money(Required(configuration.FlatFeeMinor, "flatFeeMinor"), currency)),
            PricingModel.PerUnit => new PerUnitPricing(
                new Money(Required(configuration.UnitPriceMinor, "unitPriceMinor"), currency)),
            PricingModel.Tiered => new TieredPricing(BuildTiers(configuration, currency)),
            PricingModel.Volume => new VolumePricing(BuildTiers(configuration, currency)),
            PricingModel.GraduatedWithOverage => new GraduatedOveragePricing(
                new Money(Required(configuration.FlatFeeMinor, "flatFeeMinor"), currency),
                Required(configuration.IncludedUnits, "includedUnits"),
                new Money(Required(configuration.UnitPriceMinor, "unitPriceMinor"), currency)),
            PricingModel.Package => new PackagePricing(
                Required(configuration.BlockSize, "blockSize"),
                new Money(Required(configuration.BlockPriceMinor, "blockPriceMinor"), currency)),
            PricingModel.OneOff => new OneOffPricing(
                new Money(Required(configuration.OneOffAmountMinor, "oneOffAmountMinor"), currency)),
            _ => throw new DomainException("Unsupported pricing model.")
        };
    }

    private static IEnumerable<PriceTier> BuildTiers(
        PricingConfiguration configuration,
        string currency) =>
        (configuration.Tiers ?? throw new DomainException("Tiers are required."))
        .Select(tier => new PriceTier(tier.UpToInclusive, new Money(tier.UnitPriceMinor, currency)));

    private static long Required(long? value, string name) =>
        value ?? throw new DomainException($"{name} is required for this pricing model.");
}
