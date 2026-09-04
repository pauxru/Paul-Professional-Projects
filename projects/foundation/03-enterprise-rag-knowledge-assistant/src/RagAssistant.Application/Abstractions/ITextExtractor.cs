namespace RagAssistant.Application.Abstractions;

public interface ITextExtractor
{
    string Extract(Stream input, string contentType, string filename);
}

public interface IIdGenerator
{
    Guid NewId();
}

public sealed class GuidIdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.NewGuid();
}
