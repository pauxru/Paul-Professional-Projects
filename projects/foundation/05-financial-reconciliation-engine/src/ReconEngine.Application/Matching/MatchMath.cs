using ReconEngine.Domain.Entities;

namespace ReconEngine.Application.Matching;

/// <summary>Shared amount/date comparison helpers used by several rules.</summary>
internal static class MatchMath
{
    /// <summary>True when two minor-unit amounts agree within an absolute and/or percentage tolerance.</summary>
    public static bool WithinAmountTolerance(long a, long b, long absToleranceMinor, decimal pctTolerance)
    {
        var diff = Math.Abs(a - b);
        if (diff <= absToleranceMinor)
            return true;

        if (pctTolerance > 0m)
        {
            var basis = Math.Max(Math.Abs(a), Math.Abs(b));
            var allowed = (long)Math.Round(basis * (pctTolerance / 100m), 0, MidpointRounding.ToEven);
            if (diff <= allowed)
                return true;
        }

        return false;
    }

    public static int DateDiffDays(ReconRecord a, ReconRecord b) =>
        Math.Abs(a.ValueDate.DayNumber - b.ValueDate.DayNumber);

    /// <summary>Confidence for a fuzzy match: 1.0 when identical, decaying with amount and date distance.</summary>
    public static decimal FuzzyConfidence(long amountDiff, long basisMinor, int dateDiffDays, int windowDays)
    {
        var amountFactor = basisMinor == 0 ? 1m : 1m - Math.Min(1m, (decimal)amountDiff / Math.Max(1, basisMinor));
        var dateFactor = windowDays == 0 ? 1m : 1m - Math.Min(1m, (decimal)dateDiffDays / Math.Max(1, windowDays));
        var score = 0.5m + 0.5m * (0.6m * amountFactor + 0.4m * dateFactor);
        return Math.Round(Math.Clamp(score, 0.5m, 0.95m), 4);
    }
}
