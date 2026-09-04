namespace ZeroTrust.Application.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
    DateTimeOffset UtcNowOffset { get; }
}
