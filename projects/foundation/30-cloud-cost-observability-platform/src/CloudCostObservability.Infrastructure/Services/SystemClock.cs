using CloudCostObservability.Application.Contracts;

namespace CloudCostObservability.Infrastructure.Services;

public sealed class SystemClock : IClock
{
    public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

