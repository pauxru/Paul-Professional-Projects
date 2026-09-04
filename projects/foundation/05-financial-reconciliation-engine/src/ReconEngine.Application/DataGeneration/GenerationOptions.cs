namespace ReconEngine.Application.DataGeneration;

/// <summary>
/// Declarative description of a synthetic dataset: how many clean rows, which random seed and which
/// defect classes to inject. Each defect class is isolated (by value date and reference) so that the
/// reconciliation engine detects an <b>exact, known</b> number of each — this is what lets the tests
/// assert precise detection counts rather than fuzzy ranges.
/// </summary>
public sealed record GenerationOptions
{
    /// <summary>Target number of internal rows. Clean pairs fill whatever is left after defects.</summary>
    public int Rows { get; init; } = 10_000;

    public int Seed { get; init; } = 42;

    public bool Duplicates { get; init; }
    public bool AmountMismatch { get; init; }
    public bool Missing { get; init; }
    public bool Fees { get; init; }
    public bool Refunds { get; init; }
    public bool StatusMismatch { get; init; }
    public bool CurrencyMismatch { get; init; }
    public bool DateOutOfWindow { get; init; }

    public bool AnyDefects =>
        Duplicates || AmountMismatch || Missing || Fees || Refunds ||
        StatusMismatch || CurrencyMismatch || DateOutOfWindow;

    /// <summary>Enable every defect class (the default when <c>--inject</c> is omitted or set to <c>all</c>).</summary>
    public static GenerationOptions All(int rows, int seed) => new()
    {
        Rows = rows,
        Seed = seed,
        Duplicates = true,
        AmountMismatch = true,
        Missing = true,
        Fees = true,
        Refunds = true,
        StatusMismatch = true,
        CurrencyMismatch = true,
        DateOutOfWindow = true,
    };

    /// <summary>
    /// Parse a comma-separated inject list (e.g. <c>duplicates,amount-mismatch,missing,fees,refunds</c>).
    /// An empty list or the token <c>all</c> enables everything.
    /// </summary>
    public static GenerationOptions Parse(int rows, int seed, string? inject)
    {
        var tokens = (inject ?? string.Empty)
            .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();

        if (tokens.Count == 0 || tokens.Contains("all"))
            return All(rows, seed);

        return new GenerationOptions
        {
            Rows = rows,
            Seed = seed,
            Duplicates = tokens.Contains("duplicates") || tokens.Contains("duplicate"),
            AmountMismatch = tokens.Contains("amount-mismatch") || tokens.Contains("amount") || tokens.Contains("mismatch"),
            Missing = tokens.Contains("missing"),
            Fees = tokens.Contains("fees") || tokens.Contains("fee"),
            Refunds = tokens.Contains("refunds") || tokens.Contains("refund"),
            StatusMismatch = tokens.Contains("status-mismatch") || tokens.Contains("status"),
            CurrencyMismatch = tokens.Contains("currency-mismatch") || tokens.Contains("currency"),
            DateOutOfWindow = tokens.Contains("date-out-of-window") || tokens.Contains("date-window") || tokens.Contains("dateoutofwindow"),
        };
    }
}
