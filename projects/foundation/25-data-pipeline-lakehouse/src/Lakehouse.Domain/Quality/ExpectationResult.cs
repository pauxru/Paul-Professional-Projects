namespace Lakehouse.Domain.Quality;

/// <summary>Whether a failed expectation merely warns or blocks promotion.</summary>
public enum Severity
{
    Warn,
    Fail
}

/// <summary>The outcome of evaluating one expectation against a batch of rows.</summary>
public sealed record ExpectationResult(
    string Name,
    string Expectation,
    string Column,
    Severity Severity,
    bool Passed,
    long Evaluated,
    long FailedCount,
    string Message)
{
    /// <summary>A failure that must block promotion to the next layer (trips the circuit breaker).</summary>
    public bool IsBlocking => !Passed && Severity == Severity.Fail;
}

/// <summary>
/// The aggregate result of running a suite of expectations for one dataset in one run. Exposes whether
/// any blocking (Fail-severity) expectation failed — the signal the circuit breaker reads.
/// </summary>
public sealed record DataQualityReport(
    string Dataset,
    string RunId,
    DateTimeOffset EvaluatedAt,
    IReadOnlyList<ExpectationResult> Results)
{
    public bool HasBlockingFailure => Results.Any(r => r.IsBlocking);
    public bool HasWarning => Results.Any(r => !r.Passed && r.Severity == Severity.Warn);
    public int Passed => Results.Count(r => r.Passed);
    public int Failed => Results.Count(r => !r.Passed);
}
