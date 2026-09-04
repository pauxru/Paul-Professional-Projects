using Lakehouse.Domain.Data;
using Lakehouse.Domain.Quality;

namespace Lakehouse.Application.Quality;

/// <summary>A set of expectations bound to a single table.</summary>
public sealed record DatasetExpectations(string Table, IReadOnlyList<Expectation> Expectations);

/// <summary>
/// A named quality gate spanning one or more tables. Running the gate produces a single consolidated
/// <see cref="DataQualityReport"/>; if any Fail-severity expectation fails, the report is "blocking"
/// and the circuit breaker stops promotion to the next medallion layer.
/// </summary>
public sealed record QualityGate(string Name, IReadOnlyList<DatasetExpectations> Datasets);

/// <summary>Raised when a gate has a blocking failure and downstream promotion must be halted.</summary>
public sealed class CircuitBreakerException(DataQualityReport report)
    : Exception($"Circuit breaker tripped for gate '{report.Dataset}': {report.Failed} failing expectation(s), " +
                $"{report.Results.Count(r => r.IsBlocking)} blocking.")
{
    public DataQualityReport Report { get; } = report;
}

/// <summary>Trips (throws) when a report contains a blocking failure. Kept trivial and side-effect-free.</summary>
public static class CircuitBreaker
{
    public static void Assert(DataQualityReport report)
    {
        if (report.HasBlockingFailure) throw new CircuitBreakerException(report);
    }
}
