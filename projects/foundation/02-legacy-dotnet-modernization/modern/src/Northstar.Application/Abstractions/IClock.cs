namespace Northstar.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
