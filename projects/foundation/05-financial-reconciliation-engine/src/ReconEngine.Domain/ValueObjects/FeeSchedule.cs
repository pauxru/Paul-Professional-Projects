namespace ReconEngine.Domain.ValueObjects;

/// <summary>
/// A tier of a provider fee schedule. <see cref="Percent"/> is a fraction (0.029 == 2.9%).
/// The expected fee is <c>round(gross * Percent) + FixedMinor</c>. A tier whose currency is
/// <c>"*"</c> applies to any currency not otherwise listed.
/// </summary>
public sealed record FeeScheduleTier(string Currency, decimal Percent, long FixedMinor);

/// <summary>
/// Declarative fee schedule used by the fee-adjusted matching rule to compute the fee the engine
/// expects a PSP to have deducted, and by fee-variance detection.
/// </summary>
public sealed record FeeSchedule(IReadOnlyList<FeeScheduleTier> Tiers, long ToleranceMinor)
{
    /// <summary>Default demo schedule: 2.9% + 0.30 minor-unit-scaled fixed, ±1 minor unit tolerance.</summary>
    public static FeeSchedule Default { get; } = new(
        new[] { new FeeScheduleTier("*", 0.029m, 30) },
        ToleranceMinor: 1);

    public long ExpectedFeeMinor(long grossMinor, string currency)
    {
        var tier = Tiers.FirstOrDefault(t => string.Equals(t.Currency, currency, StringComparison.OrdinalIgnoreCase))
                   ?? Tiers.FirstOrDefault(t => t.Currency == "*")
                   ?? new FeeScheduleTier("*", 0m, 0);

        var variable = (long)Math.Round(Math.Abs(grossMinor) * tier.Percent, 0, MidpointRounding.ToEven);
        return variable + tier.FixedMinor;
    }
}
