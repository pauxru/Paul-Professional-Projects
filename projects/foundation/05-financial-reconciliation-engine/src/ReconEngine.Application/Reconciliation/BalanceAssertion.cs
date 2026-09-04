namespace ReconEngine.Application.Reconciliation;

/// <summary>Per-currency monetary totals used for reports and the self-checking balance assertion.</summary>
public sealed record CurrencyTotals(
    string Currency,
    long InternalMinor,
    long MatchedInternalMinor,
    long UnmatchedInternalMinor,
    long ExternalMinor,
    long MatchedExternalMinor,
    long UnmatchedExternalMinor);

/// <summary>
/// The engine's self-check: for every currency the internal total must exactly equal matched-internal
/// plus unmatched-internal. This is a partition invariant — if a record were lost or double-counted it
/// would break — so the engine fails the run loudly rather than reporting figures that do not tie out.
/// </summary>
public static class BalanceAssertion
{
    public static (bool Passed, string? Detail) Check(IEnumerable<CurrencyTotals> totals)
    {
        foreach (var t in totals)
        {
            var expected = t.MatchedInternalMinor + t.UnmatchedInternalMinor;
            if (t.InternalMinor != expected)
            {
                return (false,
                    $"Balance violation for {t.Currency}: internal {t.InternalMinor} != matched {t.MatchedInternalMinor} + unmatched {t.UnmatchedInternalMinor} ({expected}).");
            }
        }

        return (true, null);
    }
}

/// <summary>Raised when a completed run's monetary totals do not tie out, per currency.</summary>
public sealed class BalanceAssertionException : Exception
{
    public BalanceAssertionException(string message) : base(message) { }
}
