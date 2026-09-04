using System.ComponentModel.DataAnnotations;

namespace Collab.Application.Options;

/// <summary>
/// Tunables for the collaboration engine and hub abuse controls. Bound from configuration and
/// validated at startup.
/// </summary>
public sealed class CollaborationOptions
{
    public const string SectionName = "Collaboration";

    /// <summary>Maximum rendered length of a text document; operations that would exceed it are rejected.</summary>
    [Range(1, 10_000_000)]
    public int MaxDocumentLength { get; set; } = 200_000;

    /// <summary>Maximum number of primitive operations in a single submission.</summary>
    [Range(1, 100_000)]
    public int MaxOperationsPerSubmit { get; set; } = 2_000;

    /// <summary>Write a checkpoint after this many accepted operations.</summary>
    [Range(1, 1_000_000)]
    public int SnapshotEveryNOperations { get; set; } = 200;

    /// <summary>How often the presence flusher coalesces and broadcasts presence changes.</summary>
    [Range(20, 5_000)]
    public int PresenceThrottleMs { get; set; } = 150;

    /// <summary>A participant with no heartbeat for this long is marked idle.</summary>
    [Range(1, 3_600)]
    public int PresenceIdleSeconds { get; set; } = 20;

    /// <summary>A participant with no heartbeat for this long is evicted entirely.</summary>
    [Range(1, 86_400)]
    public int PresenceEvictSeconds { get; set; } = 60;

    /// <summary>Sustained per-connection operation submission rate cap (token-bucket refill/sec).</summary>
    [Range(1, 10_000)]
    public int OperationsPerSecondPerConnection { get; set; } = 50;

    /// <summary>Token-bucket burst allowance for operation submissions.</summary>
    [Range(1, 100_000)]
    public int OperationBurst { get; set; } = 200;

    /// <summary>Consecutive rate-limit violations before the connection is forcibly disconnected.</summary>
    [Range(1, 1_000)]
    public int MaxViolationsBeforeDisconnect { get; set; } = 20;
}
