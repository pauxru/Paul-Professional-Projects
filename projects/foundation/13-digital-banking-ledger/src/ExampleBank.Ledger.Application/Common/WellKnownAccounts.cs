namespace ExampleBank.Ledger.Application.Common;

/// <summary>
/// Codes for the internal GL accounts the engine relies on (seeded at startup). Centralised so the
/// seeder and the services agree on exact codes.
/// </summary>
public static class WellKnownAccounts
{
    /// <summary>Per-currency FX clearing/position account, e.g. <c>FX-CLEARING-USD</c>.</summary>
    public static string FxClearing(string currency) => $"FX-CLEARING-{currency}";

    /// <summary>Per-currency FX rounding gain/loss account, e.g. <c>FX-GAINLOSS-USD</c>.</summary>
    public static string FxGainLoss(string currency) => $"FX-GAINLOSS-{currency}";
}
