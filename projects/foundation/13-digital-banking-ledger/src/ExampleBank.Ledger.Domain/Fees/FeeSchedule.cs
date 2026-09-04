using ExampleBank.Ledger.Domain.Common;

namespace ExampleBank.Ledger.Domain.Fees;

public enum FeeType
{
    Fixed = 1,
    Percentage = 2,
    Tiered = 3,
}

/// <summary>
/// One marginal band of a tiered fee. The band applies <see cref="RateBps"/> basis points to the
/// portion of the base amount that falls between the previous threshold and <see cref="UpToMinor"/>.
/// </summary>
public sealed class FeeTier
{
    private FeeTier() { } // EF

    public Guid Id { get; private set; }
    public Guid FeeScheduleId { get; private set; }
    public int Ordinal { get; private set; }

    /// <summary>Exclusive upper bound of this band in minor units; long.MaxValue for the top band.</summary>
    public long UpToMinor { get; private set; }

    /// <summary>Marginal rate in basis points (100 bps = 1%).</summary>
    public long RateBps { get; private set; }

    public static FeeTier Create(int ordinal, long upToMinor, long rateBps)
    {
        if (rateBps < 0)
        {
            throw new DomainException("fee.invalid_tier", "Tier rate cannot be negative.");
        }

        return new FeeTier
        {
            Id = Guid.NewGuid(),
            Ordinal = ordinal,
            UpToMinor = upToMinor,
            RateBps = rateBps,
        };
    }
}

/// <summary>
/// A configurable fee schedule. Percentages are basis points (integer) so there is no floating
/// point anywhere; the result is clamped to optional min/max caps. Fees post to an income account.
/// </summary>
public sealed class FeeSchedule
{
    private readonly List<FeeTier> _tiers = new();

    private FeeSchedule() { } // EF

    public Guid Id { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public FeeType Type { get; private set; }
    public string Currency { get; private set; } = null!;
    public long FixedAmountMinor { get; private set; }
    public long RateBps { get; private set; }
    public long MinFeeMinor { get; private set; }

    /// <summary>Maximum fee in minor units; 0 means uncapped.</summary>
    public long MaxFeeMinor { get; private set; }

    public Guid IncomeAccountId { get; private set; }

    public IReadOnlyList<FeeTier> Tiers => _tiers;

    public static FeeSchedule CreateFixed(string code, string name, string currency, long fixedAmountMinor, Guid incomeAccountId)
        => Build(code, name, currency, FeeType.Fixed, incomeAccountId, fixedAmountMinor: fixedAmountMinor);

    public static FeeSchedule CreatePercentage(
        string code, string name, string currency, long rateBps, Guid incomeAccountId, long minFeeMinor = 0, long maxFeeMinor = 0)
        => Build(code, name, currency, FeeType.Percentage, incomeAccountId, rateBps: rateBps, minFeeMinor: minFeeMinor, maxFeeMinor: maxFeeMinor);

    public static FeeSchedule CreateTiered(
        string code, string name, string currency, IEnumerable<FeeTier> tiers, Guid incomeAccountId, long minFeeMinor = 0, long maxFeeMinor = 0)
    {
        var schedule = Build(code, name, currency, FeeType.Tiered, incomeAccountId, minFeeMinor: minFeeMinor, maxFeeMinor: maxFeeMinor);
        schedule._tiers.AddRange(tiers.OrderBy(t => t.Ordinal));
        if (schedule._tiers.Count == 0)
        {
            throw new DomainException("fee.no_tiers", "A tiered fee schedule needs at least one tier.");
        }

        return schedule;
    }

    private static FeeSchedule Build(
        string code, string name, string currency, FeeType type, Guid incomeAccountId,
        long fixedAmountMinor = 0, long rateBps = 0, long minFeeMinor = 0, long maxFeeMinor = 0)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new DomainException("fee.invalid_code", "Fee schedule code is required.");
        }

        if (maxFeeMinor != 0 && maxFeeMinor < minFeeMinor)
        {
            throw new DomainException("fee.invalid_caps", "Max fee cannot be less than min fee.");
        }

        return new FeeSchedule
        {
            Id = Guid.NewGuid(),
            Code = code.Trim(),
            Name = string.IsNullOrWhiteSpace(name) ? code.Trim() : name.Trim(),
            Type = type,
            Currency = currency,
            IncomeAccountId = incomeAccountId,
            FixedAmountMinor = fixedAmountMinor,
            RateBps = rateBps,
            MinFeeMinor = minFeeMinor,
            MaxFeeMinor = maxFeeMinor,
        };
    }

    /// <summary>Computes the fee in minor units for a base amount, applying the min/max caps.</summary>
    public long Calculate(long baseAmountMinor)
    {
        if (baseAmountMinor < 0)
        {
            throw new DomainException("fee.negative_base", "Fee base amount cannot be negative.");
        }

        long raw = Type switch
        {
            FeeType.Fixed => FixedAmountMinor,
            FeeType.Percentage => ApplyBps(baseAmountMinor, RateBps),
            FeeType.Tiered => CalculateTiered(baseAmountMinor),
            _ => 0,
        };

        if (raw < MinFeeMinor)
        {
            raw = MinFeeMinor;
        }

        if (MaxFeeMinor != 0 && raw > MaxFeeMinor)
        {
            raw = MaxFeeMinor;
        }

        return raw;
    }

    private long CalculateTiered(long baseAmountMinor)
    {
        long fee = 0;
        long lower = 0;
        foreach (var tier in _tiers.OrderBy(t => t.Ordinal))
        {
            if (baseAmountMinor <= lower)
            {
                break;
            }

            long bandUpper = Math.Min(baseAmountMinor, tier.UpToMinor);
            long bandSize = bandUpper - lower;
            if (bandSize > 0)
            {
                fee += ApplyBps(bandSize, tier.RateBps);
            }

            lower = tier.UpToMinor;
        }

        return fee;
    }

    /// <summary>Applies a basis-point rate to an amount with round-half-up, purely in integers.</summary>
    private static long ApplyBps(long amountMinor, long rateBps)
        => (amountMinor * rateBps + 5000) / 10000;
}
