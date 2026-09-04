namespace ReconEngine.Domain.ValueObjects;

/// <summary>
/// ISO 4217 currency metadata. Only the minor-unit exponent is needed by the engine to convert
/// between major units (what humans read) and minor units (what the ledger stores as <see cref="long"/>).
/// </summary>
public static class CurrencyInfo
{
    // Exponent = number of decimal places. KES/USD/EUR are all 2; JPY is 0; BHD is 3 (kept to prove the code is general).
    private static readonly Dictionary<string, int> Exponents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["KES"] = 2,
        ["USD"] = 2,
        ["EUR"] = 2,
        ["GBP"] = 2,
        ["JPY"] = 0,
        ["BHD"] = 3,
    };

    public static readonly IReadOnlyCollection<string> Supported = new[] { "KES", "USD", "EUR" };

    public static bool IsKnown(string currency) => Exponents.ContainsKey(currency);

    public static int DecimalsFor(string currency) =>
        Exponents.TryGetValue(currency, out var d) ? d : 2;

    /// <summary>Scale factor (10^exponent) used to move between major and minor units.</summary>
    public static decimal ScaleFor(string currency) => DecimalsFor(currency) switch
    {
        0 => 1m,
        1 => 10m,
        2 => 100m,
        3 => 1000m,
        4 => 10000m,
        _ => 100m,
    };
}
