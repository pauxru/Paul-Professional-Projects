using System.Security.Cryptography;
using System.Text;

namespace AgentPlatform.Domain.Budgets;

/// <summary>
/// Detects non-productive oscillation: the same tool invoked with the same arguments repeated
/// beyond a threshold within a run. Agents that get stuck re-issuing an identical call (a common
/// failure mode) are halted with a clear reason rather than burning the whole budget.
/// </summary>
public sealed class LoopDetector
{
    private readonly int _threshold;
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly Queue<string> _recent = new();
    private readonly int _windowSize;

    public LoopDetector(int threshold = 3, int windowSize = 12)
    {
        _threshold = threshold;
        _windowSize = windowSize;
    }

    /// <summary>
    /// Record a tool call. Returns true when the identical (tool, arguments) signature has now
    /// occurred at least <c>threshold</c> times — i.e. the run is looping.
    /// </summary>
    public bool RecordAndCheck(string toolName, string canonicalArguments)
    {
        var signature = Signature(toolName, canonicalArguments);
        var count = _counts.GetValueOrDefault(signature) + 1;
        _counts[signature] = count;

        _recent.Enqueue(signature);
        if (_recent.Count > _windowSize)
        {
            var evicted = _recent.Dequeue();
            if (--_counts[evicted] <= 0) _counts.Remove(evicted);
        }

        return count >= _threshold;
    }

    public int SeenCount(string toolName, string canonicalArguments)
        => _counts.GetValueOrDefault(Signature(toolName, canonicalArguments));

    private static string Signature(string toolName, string canonicalArguments)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{toolName}\u0001{canonicalArguments}"));
        return Convert.ToHexString(bytes);
    }
}
