namespace FraudPipeline.Domain.Abstractions;

public interface IIdGenerator
{
    Guid NewGuid();
}

public sealed class GuidIdGenerator : IIdGenerator
{
    public Guid NewGuid() => Guid.NewGuid();
}
