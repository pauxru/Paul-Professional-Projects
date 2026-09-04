using JobScheduler.Application.Abstractions;

namespace JobScheduler.Infrastructure.Time;

/// <summary>Production clock backed by the wall clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
