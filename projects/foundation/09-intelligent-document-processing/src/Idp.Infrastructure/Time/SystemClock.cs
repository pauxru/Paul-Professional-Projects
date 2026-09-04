using Idp.Application.Abstractions;

namespace Idp.Infrastructure.Time;

/// <summary>Production clock backed by the OS. Always returns UTC.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
