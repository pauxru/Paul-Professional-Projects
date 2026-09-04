namespace ExampleBank.Ledger.Application.Abstractions;

/// <summary>
/// Abstracts the current time so domain and application logic never call
/// <see cref="DateTimeOffset.UtcNow"/> directly. Tests substitute a deterministic FakeClock.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);
}
