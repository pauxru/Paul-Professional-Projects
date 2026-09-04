namespace Lakehouse.Application.Abstractions;

/// <summary>
/// Abstraction over "now". Domain and pipeline code never call <see cref="DateTimeOffset.UtcNow"/>
/// directly so that time-dependent behaviour (freshness SLAs, valid-from stamps) is deterministic in tests.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
