using System.ComponentModel.DataAnnotations;

namespace JobScheduler.Application.Options;

/// <summary>
/// Tunables for the coordination engine. Bound from configuration and validated at startup.
/// Durations are in seconds so they are easy to express in JSON / environment variables.
/// </summary>
public sealed class EngineOptions
{
    public const string SectionName = "Engine";

    /// <summary>How long a claim lease is valid before it must be renewed by heartbeat.</summary>
    [Range(1, 3600)] public int LeaseSeconds { get; set; } = 30;

    /// <summary>Heartbeat cadence while a run executes; must be well under the lease.</summary>
    [Range(1, 3600)] public int HeartbeatSeconds { get; set; } = 10;

    /// <summary>Worker poll interval when looking for due work.</summary>
    [Range(1, 600)] public int PollSeconds { get; set; } = 2;

    /// <summary>Leader lease TTL.</summary>
    [Range(1, 3600)] public int LeaderTtlSeconds { get; set; } = 15;

    /// <summary>How often the leader runs its singleton duties.</summary>
    [Range(1, 600)] public int LeaderLoopSeconds { get; set; } = 5;

    /// <summary>A worker node whose heartbeat is older than this is considered dead.</summary>
    [Range(1, 3600)] public int NodeTtlSeconds { get; set; } = 45;

    /// <summary>Global cap on concurrently executing runs across the fleet.</summary>
    [Range(1, 10000)] public int GlobalMaxConcurrency { get; set; } = 64;

    /// <summary>Optional per-queue worker-slot caps (queue name -&gt; max active). Absent = unlimited.</summary>
    public Dictionary<string, int> QueueSlots { get; set; } = new();

    /// <summary>Priority aging boost applied per minute of waiting (0 disables aging).</summary>
    [Range(0, 1000)] public double PriorityAgingPerMinute { get; set; } = 1.0;

    /// <summary>Succeeded/terminal runs older than this are pruned by the retention job.</summary>
    [Range(1, 100000)] public int RetentionMinutes { get; set; } = 1440;

    /// <summary>Consecutive failures for a job definition before its circuit opens.</summary>
    [Range(1, 1000)] public int CircuitFailureThreshold { get; set; } = 5;

    /// <summary>Cooldown for which a tripped circuit stays open.</summary>
    [Range(1, 3600)] public int CircuitCooldownSeconds { get; set; } = 60;

    public TimeSpan Lease => TimeSpan.FromSeconds(LeaseSeconds);
    public TimeSpan Heartbeat => TimeSpan.FromSeconds(HeartbeatSeconds);
    public TimeSpan Poll => TimeSpan.FromSeconds(PollSeconds);
    public TimeSpan LeaderTtl => TimeSpan.FromSeconds(LeaderTtlSeconds);
    public TimeSpan LeaderLoop => TimeSpan.FromSeconds(LeaderLoopSeconds);
    public TimeSpan NodeTtl => TimeSpan.FromSeconds(NodeTtlSeconds);
    public TimeSpan Retention => TimeSpan.FromMinutes(RetentionMinutes);
    public TimeSpan CircuitCooldown => TimeSpan.FromSeconds(CircuitCooldownSeconds);
}
