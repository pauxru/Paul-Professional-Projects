namespace JobScheduler.Application.Options;

/// <summary>
/// Identity and capabilities of the local node when it runs a worker loop. The worker host sets
/// <see cref="NodeId"/> from its <c>--node-id</c> argument; the API host can optionally run an
/// in-process worker for single-process demos and tests.
/// </summary>
public sealed class NodeOptions
{
    public const string SectionName = "Node";

    /// <summary>Stable id of this node. Defaults to a generated value if unset.</summary>
    public string NodeId { get; set; } = $"node-{Guid.NewGuid():N}".Substring(0, 12);

    /// <summary>Capability tags this node advertises; jobs with required tags only run here if matched.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Max concurrent runs this node will execute.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Whether this host runs the worker claim/execute loop.</summary>
    public bool RunWorker { get; set; } = true;

    /// <summary>Whether this host participates in leader election and runs singleton duties.</summary>
    public bool RunLeader { get; set; } = true;
}
