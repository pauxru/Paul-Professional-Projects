namespace JobScheduler.Domain.Entities;

/// <summary>
/// A registered worker node. Heartbeats keep it alive; a node whose heartbeat age exceeds the
/// TTL is considered dead and its in-flight runs are reclaimed. Tags express capabilities so a
/// run only lands on a node that can execute it.
/// </summary>
public sealed class WorkerNode
{
    private WorkerNode() { }

    public string NodeId { get; private set; } = string.Empty;
    public string Hostname { get; private set; } = string.Empty;
    public WorkerStatus Status { get; private set; } = WorkerStatus.Active;
    public string TagsCsv { get; private set; } = string.Empty;
    public int MaxConcurrency { get; private set; } = 4;
    public DateTimeOffset RegisteredAt { get; private set; }
    public DateTimeOffset LastHeartbeat { get; private set; }

    public IReadOnlyList<string> Tags =>
        TagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static WorkerNode Register(
        string nodeId,
        string hostname,
        IEnumerable<string> tags,
        int maxConcurrency,
        DateTimeOffset now) => new()
        {
            NodeId = Guard.NotBlank(nodeId, nameof(nodeId)),
            Hostname = hostname,
            TagsCsv = string.Join(',', tags),
            MaxConcurrency = Math.Max(1, maxConcurrency),
            Status = WorkerStatus.Active,
            RegisteredAt = now,
            LastHeartbeat = now
        };

    public void Heartbeat(DateTimeOffset now)
    {
        LastHeartbeat = now;
        if (Status == WorkerStatus.Dead)
        {
            Status = WorkerStatus.Active;
        }
    }

    public void BeginDraining(DateTimeOffset now)
    {
        Status = WorkerStatus.Draining;
        LastHeartbeat = now;
    }

    public void MarkDead() => Status = WorkerStatus.Dead;

    public bool IsAlive(DateTimeOffset now, TimeSpan ttl) =>
        Status != WorkerStatus.Dead && now - LastHeartbeat <= ttl;

    public TimeSpan HeartbeatAge(DateTimeOffset now) => now - LastHeartbeat;

    /// <summary>True when this node can run a job requiring <paramref name="requiredTags"/>.</summary>
    public bool CanRun(IReadOnlyCollection<string> requiredTags)
    {
        if (requiredTags.Count == 0)
        {
            return true;
        }
        var mine = new HashSet<string>(Tags, StringComparer.OrdinalIgnoreCase);
        return requiredTags.All(mine.Contains);
    }
}
