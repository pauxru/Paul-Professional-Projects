namespace SampleApi.Pathologies;

/// <summary>
/// Central switch registry. Modes can be flipped at runtime via <c>POST /admin/pathology</c>
/// (dev only) or by setting a header on individual requests so a load test can exercise
/// the same host with different pathologies in successive scenarios.
///
/// The header override is namespaced <c>X-Pathology-*</c> and the values are documented in
/// <c>docs/methodology.md</c>. This is the crucial mechanism the case study relies on.
/// </summary>
public sealed class PathologyState
{
    public bool NPlusOneQueries { get; set; }
    public bool MissingIndex { get; set; }
    public int DownstreamLatencyMs { get; set; }
    public bool LockContention { get; set; }
    public bool MemoryPressureLeak { get; set; }
    public bool Optimised { get; set; }

    public PathologyState Snapshot() => new()
    {
        NPlusOneQueries = NPlusOneQueries,
        MissingIndex = MissingIndex,
        DownstreamLatencyMs = DownstreamLatencyMs,
        LockContention = LockContention,
        MemoryPressureLeak = MemoryPressureLeak,
        Optimised = Optimised,
    };
}

public static class PathologyHeaders
{
    public const string NPlusOne = "X-Pathology-NPlusOne";
    public const string MissingIndex = "X-Pathology-MissingIndex";
    public const string DownstreamLatencyMs = "X-Pathology-DownstreamLatencyMs";
    public const string LockContention = "X-Pathology-LockContention";
    public const string MemoryLeak = "X-Pathology-MemoryLeak";
    public const string Optimised = "X-Pathology-Optimised";
}

/// <summary>
/// PATCH-style DTO for <c>POST /admin/pathology</c>: only the properties present in the
/// request body are applied to the singleton <see cref="PathologyState"/> — the rest keep
/// their current value. This lets a scenario flip one mode without touching the others.
/// </summary>
public sealed class PathologyPatch
{
    public bool? NPlusOneQueries { get; set; }
    public bool? MissingIndex { get; set; }
    public int? DownstreamLatencyMs { get; set; }
    public bool? LockContention { get; set; }
    public bool? MemoryPressureLeak { get; set; }
    public bool? Optimised { get; set; }
}
