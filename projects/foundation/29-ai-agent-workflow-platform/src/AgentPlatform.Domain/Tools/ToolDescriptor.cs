using AgentPlatform.Domain.Json;

namespace AgentPlatform.Domain.Tools;

/// <summary>
/// Immutable metadata that fully describes a statically-registered tool. The registry is a
/// closed allow-list: a tool that is not described here cannot be invoked, and its parameter
/// contract (<see cref="ParameterSchema"/>) is enforced before execution.
/// </summary>
public sealed class ToolDescriptor
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Description { get; init; }

    /// <summary>JSON-schema contract for the arguments. Validated before every execution.</summary>
    public required JsonSchema ParameterSchema { get; init; }

    /// <summary>Optional JSON-schema contract the tool's output is checked against.</summary>
    public JsonSchema? OutputSchema { get; init; }

    public required ToolSideEffect SideEffect { get; init; }
    public ToolRiskLevel RiskLevel { get; init; } = ToolRiskLevel.Low;

    /// <summary>Scopes the caller must hold. Missing scope ⇒ a structured unauthorised error.</summary>
    public IReadOnlyList<string> RequiredScopes { get; init; } = Array.Empty<string>();

    public int RateLimitPerMinute { get; init; } = 120;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Relative cost weight, charged against the run/tenant cost budget.</summary>
    public decimal CostWeight { get; init; } = 0m;

    /// <summary>True when a call must pause for human approval before executing.</summary>
    public bool RequiresApproval { get; init; }

    /// <summary>
    /// Whether repeated calls with identical arguments are naturally idempotent. Mutating tools
    /// that are not naturally idempotent are protected by a persisted idempotency key.
    /// </summary>
    public bool IsNaturallyIdempotent { get; init; } = true;

    public bool IsMutating => SideEffect is ToolSideEffect.Mutating;
}
