namespace Healthcare.Application.Abstractions;

/// <summary>Deterministic id generator port for testability.</summary>
public interface IIdGenerator
{
    Guid NewId();
}

public sealed class GuidIdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.NewGuid();
}
