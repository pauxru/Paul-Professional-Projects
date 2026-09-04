using ReconEngine.Domain.Enums;

namespace ReconEngine.Application.Reconciliation;

/// <summary>Rules for deciding whether the statuses on two matched lines contradict each other.</summary>
public static class StatusRules
{
    private static bool Notable(TransactionStatus s) =>
        s is TransactionStatus.Refunded or TransactionStatus.Reversed
          or TransactionStatus.Failed or TransactionStatus.ChargedBack;

    /// <summary>
    /// Two statuses contradict when exactly one is a terminal/negative state, or both are negative but
    /// different (e.g. Refunded vs Reversed). In-force states (Captured/Settled/Pending) and Unknown are
    /// treated as compatible so that normal lifecycle progression does not raise noise.
    /// </summary>
    public static bool Contradictory(TransactionStatus a, TransactionStatus b)
    {
        if (a == TransactionStatus.Unknown || b == TransactionStatus.Unknown)
            return false;
        if (Notable(a) != Notable(b))
            return true;
        return Notable(a) && Notable(b) && a != b;
    }
}
