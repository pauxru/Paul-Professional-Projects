using Lakehouse.Domain.Data;
using Lakehouse.Domain.Quality;

namespace Lakehouse.UnitTests;

/// <summary>
/// Every declarative expectation type, exercised for both a passing and a failing batch. These are pure
/// functions of the rows, so they are trivially deterministic.
/// </summary>
public sealed class ExpectationTests
{
    private static IReadOnlyList<Row> Rows(params Row[] rows) => rows;

    [Fact]
    public void NotNull_passes_and_fails()
    {
        var e = new NotNullExpectation("x");
        Assert.True(e.Evaluate(Rows(Row.Of(("x", "a")), Row.Of(("x", "b")))).Passed);
        var fail = e.Evaluate(Rows(Row.Of(("x", "a")), Row.Of(("x", (object?)null))));
        Assert.False(fail.Passed);
        Assert.Equal(1, fail.FailedCount);
    }

    [Fact]
    public void Unique_passes_and_fails()
    {
        var e = new UniqueExpectation(new[] { "id" });
        Assert.True(e.Evaluate(Rows(Row.Of(("id", "a")), Row.Of(("id", "b")))).Passed);
        Assert.False(e.Evaluate(Rows(Row.Of(("id", "a")), Row.Of(("id", "a")))).Passed);
    }

    [Fact]
    public void AcceptedRange_passes_and_fails()
    {
        var e = new AcceptedRangeExpectation("q", 1, null);
        Assert.True(e.Evaluate(Rows(Row.Of(("q", 5L)), Row.Of(("q", 1L)))).Passed);
        Assert.False(e.Evaluate(Rows(Row.Of(("q", 0L)))).Passed);
    }

    [Fact]
    public void AcceptedValues_passes_and_fails()
    {
        var e = new AcceptedValuesExpectation("c", new[] { "web", "store" });
        Assert.True(e.Evaluate(Rows(Row.Of(("c", "web")), Row.Of(("c", "store")))).Passed);
        Assert.False(e.Evaluate(Rows(Row.Of(("c", "carrier-pigeon")))).Passed);
    }

    [Fact]
    public void ReferentialIntegrity_passes_and_fails()
    {
        var e = new ReferentialIntegrityExpectation("fk", new[] { "A", "B" });
        Assert.True(e.Evaluate(Rows(Row.Of(("fk", "A")), Row.Of(("fk", "B")))).Passed);
        var fail = e.Evaluate(Rows(Row.Of(("fk", "A")), Row.Of(("fk", "Z"))));
        Assert.False(fail.Passed);
        Assert.Equal(1, fail.FailedCount);
    }

    [Fact]
    public void Freshness_passes_and_fails()
    {
        var asOf = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var e = new FreshnessExpectation("ts", TimeSpan.FromHours(24), asOf);
        Assert.True(e.Evaluate(Rows(Row.Of(("ts", asOf.AddHours(-1))))).Passed);
        Assert.False(e.Evaluate(Rows(Row.Of(("ts", asOf.AddDays(-3))))).Passed);
    }

    [Fact]
    public void RowCountAnomaly_passes_within_tolerance_and_fails_outside()
    {
        var e = new RowCountAnomalyExpectation(baselineMean: 100, tolerance: 0.5, Severity.Fail);
        Assert.True(e.Evaluate(Enumerable.Range(0, 100).Select(_ => new Row()).ToList()).Passed);
        Assert.False(e.Evaluate(Enumerable.Range(0, 10).Select(_ => new Row()).ToList()).Passed);
    }

    [Fact]
    public void RowCountAnomaly_skips_when_no_baseline()
    {
        var e = new RowCountAnomalyExpectation(baselineMean: 0, tolerance: 0.5, Severity.Fail);
        Assert.True(e.Evaluate(Enumerable.Range(0, 999).Select(_ => new Row()).ToList()).Passed);
    }

    [Fact]
    public void DistributionDrift_passes_and_fails()
    {
        var baseline = new Dictionary<string, double> { ["A"] = 0.5, ["B"] = 0.5 };
        var e = new DistributionDriftExpectation("c", baseline, threshold: 0.2);

        var balanced = Rows(Row.Of(("c", "A")), Row.Of(("c", "B")), Row.Of(("c", "A")), Row.Of(("c", "B")));
        Assert.True(e.Evaluate(balanced).Passed);

        var skewed = Rows(Row.Of(("c", "A")), Row.Of(("c", "A")), Row.Of(("c", "A")), Row.Of(("c", "A")));
        Assert.False(e.Evaluate(skewed).Passed);
    }

    [Fact]
    public void Blocking_flag_is_only_set_for_failed_fail_severity()
    {
        var warn = new NotNullExpectation("x", Severity.Warn).Evaluate(Rows(Row.Of(("x", (object?)null))));
        var fail = new NotNullExpectation("x", Severity.Fail).Evaluate(Rows(Row.Of(("x", (object?)null))));
        Assert.False(warn.IsBlocking);
        Assert.True(fail.IsBlocking);
    }
}
