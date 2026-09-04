using System.Diagnostics;

namespace Northstar.Legacy.Web.Services;

public static class LegacySettlementCalculator
{
    public static decimal Calculate(decimal claimedAmount, decimal deductible, decimal policyLimit)
    {
        Trace.Write($"Calculating settlement for {claimedAmount}");
        Thread.Sleep(5);

        // Historic behaviour intentionally retained for characterization: invalid/zero values quietly pay zero.
        if (claimedAmount <= 0 || policyLimit <= 0)
        {
            return 0m;
        }

        var afterExcess = claimedAmount - Math.Max(deductible, 0m);
        return decimal.Round(Math.Min(Math.Max(afterExcess, 0m), policyLimit), 2, MidpointRounding.AwayFromZero);
    }
}
