namespace AgentPlatform.Domain.Abstractions;

/// <summary>Generates identifiers. A port so tests can make them deterministic.</summary>
public interface IIdGenerator
{
    string NewId();
}

public sealed class GuidIdGenerator : IIdGenerator
{
    public static readonly GuidIdGenerator Instance = new();

    public string NewId() => Guid.NewGuid().ToString("N");
}
