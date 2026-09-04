using ReconEngine.Application.Matching;
using ReconEngine.Application.Matching.Rules;
using ReconEngine.Domain.Enums;
using ReconEngine.Domain.ValueObjects;
using ReconEngine.UnitTests.TestKit;

namespace ReconEngine.UnitTests.Matching;

/// <summary>Each matching rule exercised in isolation with hand-built fixtures.</summary>
public sealed class MatchingRuleTests
{
    private static readonly MatchingRuleSetDefinition Def = MatchingRuleSetDefinition.Default;

    // ---- Rule 1: exact reference ----

    [Fact]
    public void Exact_reference_matches_on_reference_amount_currency()
    {
        var pool = Recs.Pool(
            new[] { Recs.Internal("TXN_100", 10_000, "KES") },
            new[] { Recs.External("STL_100", 10_000, "KES") });

        var matches = new ExactReferenceMatchRule().Apply(pool, Def).ToList();

        var m = Assert.Single(matches);
        Assert.Equal(MatchKind.OneToOne, m.Kind);
        Assert.Equal(1.0m, m.Confidence);
        Assert.Contains("reference ==", m.Explanation);
    }

    [Fact]
    public void Exact_reference_does_not_match_when_amount_differs()
    {
        var pool = Recs.Pool(
            new[] { Recs.Internal("TXN_100", 10_000) },
            new[] { Recs.External("STL_100", 10_001) });

        Assert.Empty(new ExactReferenceMatchRule().Apply(pool, Def));
    }

    [Fact]
    public void Exact_reference_never_matches_across_currencies()
    {
        var pool = Recs.Pool(
            new[] { Recs.Internal("TXN_100", 10_000, "KES") },
            new[] { Recs.External("STL_100", 10_000, "USD") });

        Assert.Empty(new ExactReferenceMatchRule().Apply(pool, Def));
    }

    // ---- Rule 2: composite (merchant ref + amount + date window) ----

    [Fact]
    public void Composite_matches_on_merchant_reference_within_date_window()
    {
        var pool = Recs.Pool(
            new[] { Recs.Internal("TXN_1", 10_000, merchant: "MOID_9", date: Recs.Day(2024, 1, 10)) },
            new[] { Recs.External("STL_2", 10_000, merchant: "MOID_9", date: Recs.Day(2024, 1, 12)) });

        var m = Assert.Single(new CompositeMatchRule().Apply(pool, Def));
        Assert.Equal(MatchKind.OneToOne, m.Kind);
        Assert.Equal(0.9m, m.Confidence);
    }

    [Fact]
    public void Composite_does_not_match_outside_date_window()
    {
        var pool = Recs.Pool(
            new[] { Recs.Internal("TXN_1", 10_000, merchant: "MOID_9", date: Recs.Day(2024, 1, 10)) },
            new[] { Recs.External("STL_2", 10_000, merchant: "MOID_9", date: Recs.Day(2024, 1, 20)) });

        Assert.Empty(new CompositeMatchRule().Apply(pool, Def));
    }

    // ---- Rule 3: fuzzy amount + date window, with tolerance boundaries ----

    [Theory]
    [InlineData(50, true)]   // exactly on the absolute tolerance -> inside
    [InlineData(51, false)]  // one minor unit past it -> outside
    public void Fuzzy_absolute_amount_tolerance_boundary(long delta, bool shouldMatch)
    {
        var pool = Recs.Pool(
            new[] { Recs.Internal("A", 10_000) },
            new[] { Recs.External("B", 10_000 + delta) });

        var matches = new AmountAndDateWindowMatchRule().Apply(pool, Def).ToList();
        Assert.Equal(shouldMatch, matches.Count == 1);
    }

    [Theory]
    [InlineData(101, true)]   // 1% is taken of the larger amount (10,101 -> allowed 101) -> inside
    [InlineData(102, false)]  // one minor unit past that -> outside
    public void Fuzzy_percentage_amount_tolerance_boundary(long delta, bool shouldMatch)
    {
        var def = Def with { AmountToleranceMinor = 0, AmountTolerancePercent = 1m };
        var pool = Recs.Pool(
            new[] { Recs.Internal("A", 10_000) },
            new[] { Recs.External("B", 10_000 + delta) });

        var matches = new AmountAndDateWindowMatchRule().Apply(pool, def).ToList();
        Assert.Equal(shouldMatch, matches.Count == 1);
    }

    [Fact]
    public void Fuzzy_does_not_match_outside_date_window()
    {
        var pool = Recs.Pool(
            new[] { Recs.Internal("A", 10_000, date: Recs.Day(2024, 1, 10)) },
            new[] { Recs.External("B", 10_000, date: Recs.Day(2024, 1, 20)) });

        Assert.Empty(new AmountAndDateWindowMatchRule().Apply(pool, Def));
    }

    [Fact]
    public void Fuzzy_ignores_negative_amounts()
    {
        var pool = Recs.Pool(
            new[] { Recs.Internal("A", -10_000) },
            new[] { Recs.External("B", -10_000) });

        Assert.Empty(new AmountAndDateWindowMatchRule().Apply(pool, Def));
    }

    // ---- Rule 4: subset-sum many-to-one / one-to-many ----

    [Fact]
    public void ManyToOne_sums_multiple_internals_to_one_external()
    {
        var pool = Recs.Pool(
            new[] { Recs.Internal("A", 1_000, line: 1), Recs.Internal("B", 2_000, line: 2) },
            new[] { Recs.External("S", 3_000) });

        var m = Assert.Single(new ManyToOneMatchRule().Apply(pool, Def));
        Assert.Equal(MatchKind.ManyToOne, m.Kind);
        Assert.Equal(2, m.Internals.Count);
        Assert.Single(m.Externals);
    }

    [Fact]
    public void OneToMany_splits_one_internal_across_externals()
    {
        var pool = Recs.Pool(
            new[] { Recs.Internal("A", 3_000) },
            new[] { Recs.External("S1", 1_000, line: 1), Recs.External("S2", 2_000, line: 2) });

        var m = Assert.Single(new OneToManyMatchRule().Apply(pool, Def));
        Assert.Equal(MatchKind.OneToMany, m.Kind);
        Assert.Single(m.Internals);
        Assert.Equal(2, m.Externals.Count);
    }

    [Fact]
    public void ManyToOne_respects_group_size_cap()
    {
        // Five 1,000 internals summing to a 5,000 external cannot be formed with a cap of 4.
        var internals = Enumerable.Range(0, 5).Select(i => Recs.Internal($"A{i}", 1_000, line: i)).ToArray();
        var pool = Recs.Pool(internals, new[] { Recs.External("S", 5_000) });
        var def = Def with { SubsetSumMaxGroupSize = 4 };

        Assert.Empty(new ManyToOneMatchRule().Apply(pool, def));
    }

    // ---- Rule 5: fee-adjusted + fee variance ----

    [Fact]
    public void FeeAdjusted_matches_when_gross_equals_net_plus_scheduled_fee()
    {
        var gross = 100_000L;
        var fee = FeeSchedule.Default.ExpectedFeeMinor(gross, "KES"); // 2.9% + 30
        var pool = Recs.Pool(
            new[] { Recs.Internal("TXN_7", gross) },
            new[] { Recs.External("STL_7", gross - fee, fee: fee) });

        var m = Assert.Single(new FeeAdjustedMatchRule().Apply(pool, Def));
        Assert.Equal(MatchKind.FeeAdjusted, m.Kind);
        Assert.Null(m.FeeVarianceMinor); // fee on schedule -> no variance
    }

    [Fact]
    public void FeeAdjusted_flags_variance_when_fee_off_schedule()
    {
        var gross = 100_000L;
        var expected = FeeSchedule.Default.ExpectedFeeMinor(gross, "KES");
        var actual = expected + 200; // clearly beyond the ±1 tolerance
        var pool = Recs.Pool(
            new[] { Recs.Internal("TXN_7", gross) },
            new[] { Recs.External("STL_7", gross - actual, fee: actual) });

        var m = Assert.Single(new FeeAdjustedMatchRule().Apply(pool, Def));
        Assert.Equal(200L, m.FeeVarianceMinor);
    }

    // ---- Rule 6: refund ----

    [Fact]
    public void Refund_pairs_negative_amounts_within_window()
    {
        var pool = Recs.Pool(
            new[] { Recs.Internal("TXN_9", -5_000, status: TransactionStatus.Refunded) },
            new[] { Recs.External("STL_X", -5_000, status: TransactionStatus.Refunded) });

        var m = Assert.Single(new RefundMatchRule().Apply(pool, Def));
        Assert.Equal(MatchKind.Refund, m.Kind);
    }

    [Fact]
    public void Refund_never_pairs_across_currencies()
    {
        var pool = Recs.Pool(
            new[] { Recs.Internal("TXN_9", -5_000, "KES", status: TransactionStatus.Refunded) },
            new[] { Recs.External("STL_X", -5_000, "USD", status: TransactionStatus.Refunded) });

        Assert.Empty(new RefundMatchRule().Apply(pool, Def));
    }

    // ---- Engine-level: priority + claimed-record removal + currency isolation ----

    [Fact]
    public void Engine_prefers_exact_over_fuzzy_and_never_double_claims()
    {
        var internals = new[] { Recs.Internal("TXN_100", 10_000) };
        var externals = new[]
        {
            Recs.External("STL_100", 10_000),      // exact
            Recs.External("OTHER", 10_010),        // fuzzy candidate (within 50)
        };

        var outcome = MatchingEngine.CreateDefault().Run(internals, externals, Def);

        var m = Assert.Single(outcome.Matches);
        Assert.Equal("exact-reference", m.RuleId);
        Assert.Single(outcome.UnmatchedExternal); // the fuzzy candidate is left over, not double-claimed
    }

    [Fact]
    public void Engine_never_matches_across_currencies()
    {
        var internals = new[] { Recs.Internal("TXN_100", 10_000, "KES") };
        var externals = new[] { Recs.External("STL_100", 10_000, "USD") };

        var outcome = MatchingEngine.CreateDefault().Run(internals, externals, Def);

        Assert.Empty(outcome.Matches);
        Assert.Single(outcome.UnmatchedInternal);
        Assert.Single(outcome.UnmatchedExternal);
    }
}
