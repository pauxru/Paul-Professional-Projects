using ExampleBank.Ledger.Application.Abstractions;

namespace ExampleBank.Ledger.Infrastructure.Time;

/// <summary>Production clock backed by the wall clock in UTC.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
