using Lakehouse.Application.Quality;
using Lakehouse.Domain.Quality;

namespace Lakehouse.UnitTests;

/// <summary>The circuit breaker: it trips (throws) only on a blocking Fail-severity failure, and lets
/// warnings pass so promotion continues.</summary>
public sealed class CircuitBreakerTests
{
    private static DataQualityReport Report(params ExpectationResult[] results)
        => new("silver", "r1", DateTimeOffset.UnixEpoch, results);

    private static ExpectationResult Result(bool passed, Severity severity)
        => new("chk", "kind", "col", severity, passed, 100, passed ? 0 : 5, "msg");

    [Fact]
    public void Trips_on_blocking_fail()
    {
        var report = Report(Result(true, Severity.Fail), Result(false, Severity.Fail));
        Assert.True(report.HasBlockingFailure);
        Assert.Throws<CircuitBreakerException>(() => CircuitBreaker.Assert(report));
    }

    [Fact]
    public void Does_not_trip_on_warnings_only()
    {
        var report = Report(Result(true, Severity.Fail), Result(false, Severity.Warn));
        Assert.False(report.HasBlockingFailure);
        Assert.True(report.HasWarning);
        CircuitBreaker.Assert(report); // must not throw
    }

    [Fact]
    public void All_passing_report_is_clean()
    {
        var report = Report(Result(true, Severity.Fail), Result(true, Severity.Warn));
        Assert.False(report.HasBlockingFailure);
        Assert.False(report.HasWarning);
        Assert.Equal(0, report.Failed);
    }
}
