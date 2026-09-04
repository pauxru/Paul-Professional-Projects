using Contoso.Payments.Application.Abstractions;

namespace Contoso.Payments.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
