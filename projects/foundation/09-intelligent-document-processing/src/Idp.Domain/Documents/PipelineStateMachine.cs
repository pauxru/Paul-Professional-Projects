namespace Idp.Domain.Documents;

/// <summary>
/// Guards the document pipeline state machine. All transitions in the system must pass through
/// <see cref="EnsureCanTransition"/> so that no code path can move a document into an illegal state.
/// </summary>
public static class PipelineStateMachine
{
    private static readonly IReadOnlyDictionary<PipelineState, PipelineState[]> Allowed =
        new Dictionary<PipelineState, PipelineState[]>
        {
            [PipelineState.Received] = new[] { PipelineState.Classified, PipelineState.Failed },
            [PipelineState.Classified] = new[] { PipelineState.Extracted, PipelineState.Failed },
            [PipelineState.Extracted] = new[] { PipelineState.Validated, PipelineState.Failed },
            [PipelineState.Validated] = new[]
            {
                PipelineState.AutoApproved, PipelineState.InReview,
                PipelineState.Rejected, PipelineState.Failed
            },
            [PipelineState.AutoApproved] = new[] { PipelineState.Exported, PipelineState.Failed },
            [PipelineState.InReview] = new[]
            {
                PipelineState.Corrected, PipelineState.Exported,
                PipelineState.Rejected, PipelineState.Failed
            },
            [PipelineState.Corrected] = new[]
            {
                PipelineState.Exported, PipelineState.InReview,
                PipelineState.Rejected, PipelineState.Failed
            },
            [PipelineState.Exported] = Array.Empty<PipelineState>(),
            [PipelineState.Rejected] = Array.Empty<PipelineState>(),
            [PipelineState.Failed] = Array.Empty<PipelineState>()
        };

    /// <summary>Terminal states from which the pipeline may be restarted via reprocessing.</summary>
    public static readonly PipelineState[] Terminal =
        { PipelineState.Exported, PipelineState.Rejected, PipelineState.Failed };

    public static bool IsTerminal(PipelineState state) => Terminal.Contains(state);

    public static bool CanTransition(PipelineState from, PipelineState to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    public static IReadOnlyList<PipelineState> NextStates(PipelineState from) =>
        Allowed.TryGetValue(from, out var next) ? next : Array.Empty<PipelineState>();

    public static void EnsureCanTransition(PipelineState from, PipelineState to)
    {
        if (!CanTransition(from, to))
            throw new InvalidPipelineTransitionException(from, to);
    }
}

/// <summary>Thrown when application code attempts an illegal pipeline transition.</summary>
public sealed class InvalidPipelineTransitionException : Exception
{
    public PipelineState From { get; }
    public PipelineState To { get; }

    public InvalidPipelineTransitionException(PipelineState from, PipelineState to)
        : base($"Illegal pipeline transition {from} -> {to}.")
    {
        From = from;
        To = to;
    }
}
