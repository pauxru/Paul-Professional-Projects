namespace Lab.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
