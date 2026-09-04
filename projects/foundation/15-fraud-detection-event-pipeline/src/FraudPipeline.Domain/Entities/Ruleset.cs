namespace FraudPipeline.Domain.Entities;

/// <summary>
/// Versioned ruleset definition. Persisted as JSON so it can be replayed by
/// version to reproduce a historic decision.
/// </summary>
public sealed class Ruleset
{
    public Guid Id { get; private set; }
    public string Version { get; private set; }
    public string Name { get; private set; }
    public string DefinitionJson { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsShadow { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }

    private Ruleset()
    {
        Version = Name = DefinitionJson = "";
    }

    public Ruleset(Guid id, string version, string name, string definitionJson, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id required.", nameof(id));
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Version required.", nameof(version));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));
        if (string.IsNullOrWhiteSpace(definitionJson)) throw new ArgumentException("Definition required.", nameof(definitionJson));
        Id = id;
        Version = version;
        Name = name;
        DefinitionJson = definitionJson;
        CreatedAt = createdAt;
    }

    public void Activate(DateTimeOffset at)
    {
        IsActive = true;
        IsShadow = false;
        ActivatedAt = at;
    }

    public void Deactivate() => IsActive = false;

    public void MakeShadow()
    {
        if (IsActive) throw new InvalidOperationException("Cannot shadow an active ruleset. Deactivate first.");
        IsShadow = true;
    }

    public void ClearShadow() => IsShadow = false;
}
