namespace JobScheduler.Domain;

/// <summary>Small guard-clause helpers for domain invariants.</summary>
public static class Guard
{
    public static string NotBlank(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{paramName} must not be blank.", paramName);
        }
        return value;
    }
}

/// <summary>
/// Raised when a worker attempts to write back the result of a run using a fencing token that
/// is no longer current — i.e. its lease was lost and the run was reclaimed by another node.
/// This is the mechanism that stops a stalled worker from corrupting a reassigned run.
/// </summary>
public sealed class FencingTokenException(Guid runId, long presented, long current)
    : InvalidOperationException(
        $"Fencing token {presented} for run {runId} is stale; current token is {current}. Write rejected.")
{
    public Guid RunId { get; } = runId;
    public long Presented { get; } = presented;
    public long Current { get; } = current;
}
