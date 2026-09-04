using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.Domain.Entities;

/// <summary>
/// A persisted, versioned set of matching rules. Rulesets are immutable once created: editing
/// produces a new version so that every run can record exactly which ruleset (name + version)
/// produced its matches, for audit.
/// </summary>
public class MatchingRuleSet
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public int Version { get; set; } = 1;

    public bool IsActive { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>The serialized <see cref="MatchingRuleSetDefinition"/>.</summary>
    public string DefinitionJson { get; set; } = MatchingRuleSetDefinition.Default.Serialize();

    public DateTime CreatedAtUtc { get; set; }

    public string CreatedBy { get; set; } = "system";

    public MatchingRuleSetDefinition GetDefinition() => MatchingRuleSetDefinition.Deserialize(DefinitionJson);

    public void SetDefinition(MatchingRuleSetDefinition definition) => DefinitionJson = definition.Serialize();

    /// <summary>A short, human-readable version tag used in match audit records.</summary>
    public string VersionTag => $"{Name}@v{Version}";
}
