namespace JobScheduler.Application.Services;

/// <summary>
/// Pure admission control for starting a run. Combines global, per-definition and per-queue
/// concurrency caps with the singleton rule (never two instances of one definition at once).
/// The engine gathers the live counts and asks this gate for a yes/no.
/// </summary>
public static class ConcurrencyGate
{
    public static bool CanStart(
        int globalActive,
        int globalMax,
        int definitionActive,
        int definitionLimit,
        int queueActive,
        int queueSlots,
        bool singleton,
        bool anyActiveForDefinition)
    {
        if (globalMax > 0 && globalActive >= globalMax)
        {
            return false;
        }
        if (definitionLimit > 0 && definitionActive >= definitionLimit)
        {
            return false;
        }
        if (queueSlots > 0 && queueActive >= queueSlots)
        {
            return false;
        }
        if (singleton && anyActiveForDefinition)
        {
            return false;
        }
        return true;
    }
}
