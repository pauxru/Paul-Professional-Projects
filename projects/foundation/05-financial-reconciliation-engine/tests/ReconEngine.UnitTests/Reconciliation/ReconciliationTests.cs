using ReconEngine.Application.Exceptions;
using ReconEngine.Application.Reconciliation;
using ReconEngine.Domain.Enums;
using ReconEngine.Domain.ValueObjects;
using ReconEngine.UnitTests.TestKit;

namespace ReconEngine.UnitTests.Reconciliation;

public sealed class DuplicateDetectorTests
{
    [Fact]
    public void Duplicate_rows_are_detected_by_row_hash()
    {
        var a = Recs.Internal("TXN_1", 10_000, line: 1);
        var b = Recs.Internal("TXN_1", 10_000, line: 2); // identical identity -> same row hash
        var c = Recs.Internal("TXN_2", 20_000, line: 3);

        var (unique, duplicates) = DuplicateDetector.Detect(new[] { a, b, c });

        Assert.Equal(2, unique.Count);
        var dup = Assert.Single(duplicates);
        Assert.Equal(a.RowHash, dup.RowHash);
    }

    [Fact]
    public void Records_differing_only_in_status_are_not_duplicates()
    {
        var a = Recs.Internal("TXN_1", 10_000, status: TransactionStatus.Captured);
        var b = Recs.Internal("TXN_1", 10_000, status: TransactionStatus.Reversed);

        var (unique, duplicates) = DuplicateDetector.Detect(new[] { a, b });

        Assert.Equal(2, unique.Count);
        Assert.Empty(duplicates);
    }
}

public sealed class BalanceAssertionTests
{
    [Fact]
    public void Passes_when_internal_equals_matched_plus_unmatched()
    {
        var totals = new[]
        {
            new CurrencyTotals("KES", 10_000, 6_000, 4_000, 9_000, 6_000, 3_000),
        };

        var (passed, detail) = BalanceAssertion.Check(totals);

        Assert.True(passed);
        Assert.Null(detail);
    }

    [Fact]
    public void Fails_loudly_when_totals_do_not_tie_out()
    {
        var totals = new[]
        {
            new CurrencyTotals("KES", 10_000, 6_000, 3_999, 9_000, 6_000, 3_000), // 6000+3999 != 10000
        };

        var (passed, detail) = BalanceAssertion.Check(totals);

        Assert.False(passed);
        Assert.Contains("KES", detail);
    }
}

public sealed class StatusRulesTests
{
    [Theory]
    [InlineData(TransactionStatus.Captured, TransactionStatus.Settled, false)]
    [InlineData(TransactionStatus.Pending, TransactionStatus.Captured, false)]
    [InlineData(TransactionStatus.Reversed, TransactionStatus.Settled, true)]
    [InlineData(TransactionStatus.Refunded, TransactionStatus.Captured, true)]
    [InlineData(TransactionStatus.Refunded, TransactionStatus.Reversed, true)]  // both terminal, different
    [InlineData(TransactionStatus.Refunded, TransactionStatus.Refunded, false)] // both terminal, same
    [InlineData(TransactionStatus.Unknown, TransactionStatus.Reversed, false)]  // unknown is compatible
    public void Contradictory_status_detection(TransactionStatus a, TransactionStatus b, bool expected)
    {
        Assert.Equal(expected, StatusRules.Contradictory(a, b));
    }
}

public sealed class ExceptionClassifierTests
{
    private static readonly MatchingRuleSetDefinition Def = MatchingRuleSetDefinition.Default;
    private const long High = 1_000_000;
    private static readonly ExceptionClassifier Classifier = new();

    [Fact]
    public void Same_reference_different_amount_is_amount_mismatch()
    {
        var drafts = Classifier.Classify(
            new[] { Recs.Internal("TXN_1", 10_000) },
            new[] { Recs.External("STL_1", 20_000) },
            Def, High);

        Assert.Equal(ExceptionType.AmountMismatch, Assert.Single(drafts).Type);
    }

    [Fact]
    public void Same_reference_different_currency_is_currency_mismatch()
    {
        var drafts = Classifier.Classify(
            new[] { Recs.Internal("TXN_1", 10_000, "KES") },
            new[] { Recs.External("STL_1", 10_000, "USD") },
            Def, High);

        Assert.Equal(ExceptionType.CurrencyMismatch, Assert.Single(drafts).Type);
    }

    [Fact]
    public void Internal_with_no_external_is_missing_in_external()
    {
        var drafts = Classifier.Classify(
            new[] { Recs.Internal("TXN_1", 10_000) },
            Array.Empty<ReconEngine.Domain.Entities.ReconRecord>(),
            Def, High);

        Assert.Equal(ExceptionType.MissingInExternal, Assert.Single(drafts).Type);
    }

    [Fact]
    public void External_with_no_internal_is_missing_in_internal()
    {
        var drafts = Classifier.Classify(
            Array.Empty<ReconEngine.Domain.Entities.ReconRecord>(),
            new[] { Recs.External("STL_1", 10_000) },
            Def, High);

        Assert.Equal(ExceptionType.MissingInInternal, Assert.Single(drafts).Type);
    }

    [Fact]
    public void Shared_merchant_ref_far_apart_is_date_out_of_window()
    {
        var drafts = Classifier.Classify(
            new[] { Recs.Internal("TXN_1", 10_000, merchant: "MOID_5", date: Recs.Day(2024, 1, 1)) },
            new[] { Recs.External("STL_9", 10_000, merchant: "MOID_5", date: Recs.Day(2024, 1, 20)) },
            Def, High);

        Assert.Equal(ExceptionType.DateOutOfWindow, Assert.Single(drafts).Type);
    }
}

public sealed class CalculatorMultiCurrencyTests
{
    [Fact]
    public void Calculator_isolates_currencies_and_stays_balanced()
    {
        var calc = ReconciliationCalculator.CreateDefault();

        var internals = new[] { Recs.Internal("TXN_1", 10_000, "KES") };
        var externals = new[] { Recs.External("STL_1", 10_000, "USD") };

        var result = calc.Calculate(internals, externals, MatchingRuleSetDefinition.Default, 1_000_000);

        Assert.Empty(result.Matches);
        Assert.Contains(result.Exceptions, e => e.Type == ExceptionType.CurrencyMismatch);
        Assert.True(result.BalancePassed, result.BalanceDetail);
    }
}
