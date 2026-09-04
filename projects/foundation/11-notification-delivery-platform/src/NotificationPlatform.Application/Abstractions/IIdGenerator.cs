namespace NotificationPlatform.Application.Abstractions;

public interface IIdGenerator
{
    Guid NewId();
    string NewCorrelationId();
}

public sealed class GuidIdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.NewGuid();

    public string NewCorrelationId() => Guid.NewGuid().ToString("N");
}
