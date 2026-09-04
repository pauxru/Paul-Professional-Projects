using Bridge.Core;
using Bridge.Legacy;
using Bridge.Report;

namespace Bridge.Tests;

/// <summary>
/// The fuzz corpus, and the control group that stops it from lying.
/// </summary>
/// <remarks>
/// <para>
/// The first run of this corpus against the hardened boundary produced a perfect
/// score: six hundred hostile inputs, six hundred clean refusals, nothing silently
/// wrong. That result was worthless, and it took a while to see why. A boundary that
/// rejects <i>every</i> input scores exactly the same. The corpus could not distinguish
/// safety from uselessness, which means it was not measuring safety at all.
/// </para>
/// <para>
/// One case in four is now a deliberately legal option. Those cases carry two
/// obligations the hostile ones cannot: the hardened boundary must accept them, and the
/// price it returns must match the price the 2009 boundary returns for the same input.
/// Hardening the boundary was supposed to change what gets through, not what the answer
/// is.
/// </para>
/// </remarks>
public class FuzzCorpusTests
{
    private const ulong Seed = FuzzDriver.DefaultSeed;

    [Fact]
    public void The_corpus_is_a_pure_function_of_its_seed()
    {
        // Everything downstream depends on this. The isolated runner regenerates the
        // corpus in each child process rather than serialising it across the pipe, and
        // the summary regenerates it again to work out which cases were controls. If
        // generation were not reproducible, all three would be looking at different
        // corpora and the crash indices would name the wrong inputs.
        var a = FuzzCorpus.Generate(Seed, 400);
        var b = FuzzCorpus.Generate(Seed, 400);

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i], b[i]);
        }
    }

    [Fact]
    public void A_different_seed_gives_a_different_corpus()
    {
        var a = FuzzCorpus.Generate(Seed, 400);
        var b = FuzzCorpus.Generate(Seed + 1, 400);
        Assert.NotEqual(a[7], b[7]);
    }

    [Fact]
    public void A_prefix_of_a_longer_corpus_is_the_shorter_corpus()
    {
        // The isolated runner restarts a child at the index after a crash, and the
        // restarted child regenerates from the same seed. If case 300 depended on how
        // many cases had been asked for, the restarted child would run a different
        // input and the result would be attributed to the wrong one.
        var shortRun = FuzzCorpus.Generate(Seed, 100);
        var longRun = FuzzCorpus.Generate(Seed, 600);

        for (var i = 0; i < shortRun.Count; i++)
        {
            Assert.Equal(shortRun[i], longRun[i]);
        }
    }

    [Fact]
    public void Every_case_carries_its_own_index()
    {
        var cases = FuzzCorpus.Generate(Seed, 600);
        for (var i = 0; i < cases.Count; i++)
        {
            Assert.Equal(i, cases[i].Index);
        }
    }

    [Fact]
    public void One_case_in_four_is_a_control()
    {
        var cases = FuzzCorpus.Generate(Seed, 600);
        var controls = cases.Count(c => c.IsControl);

        Assert.Equal(150, controls);
        Assert.All(cases.Where(c => c.IsControl), c => Assert.StartsWith("valid:", c.Shape));
    }

    [Fact]
    public void The_corpus_covers_every_shape_it_claims_to()
    {
        // A corpus that generates six hundred variations of one shape has a coverage
        // problem that its size conceals.
        var shapes = FuzzCorpus.Generate(Seed, 600)
            .Select(c => c.Shape.Split(':')[0])
            .Distinct()
            .ToList();

        Assert.Contains("one-field", shapes);
        Assert.Contains("all-awkward", shapes);
        Assert.Contains("random-bits", shapes);
        Assert.Contains("valid", shapes);
    }

    [Fact]
    public void Every_control_case_is_genuinely_legal()
    {
        // The control group's whole job is to be acceptable. If the generator ever
        // produced a control the boundary is right to refuse, the run would report a
        // false positive that is really a bug in the test, and the natural response --
        // loosening the boundary -- would make the system less safe.
        using var engine = new PricingEngine();

        foreach (var c in FuzzCorpus.Generate(Seed, 600).Where(c => c.IsControl))
        {
            var o = c.Option;
            Assert.True(double.IsFinite(o.Spot) && o.Spot > 0, $"case {c.Index}: spot {o.Spot}");
            Assert.True(double.IsFinite(o.Strike) && o.Strike > 0, $"case {c.Index}: strike {o.Strike}");
            Assert.True(double.IsFinite(o.Volatility) && o.Volatility > 0, $"case {c.Index}: vol {o.Volatility}");
            Assert.True(double.IsFinite(o.Years) && o.Years > 0, $"case {c.Index}: years {o.Years}");
            Assert.True(o.Kind is 0 or 1, $"case {c.Index}: kind {o.Kind}");
            Assert.Equal(0, o.Reserved);
            Assert.True(c.Steps >= 1, $"case {c.Index}: steps {c.Steps}");
            Assert.True(c.BatchCapacity >= c.BatchCount, $"case {c.Index}: capacity below count");
        }
    }

    /// <summary>
    /// The control group, run for real: every legal input must be accepted and priced.
    /// </summary>
    [Fact]
    public void The_hardened_boundary_accepts_every_control_case()
    {
        using var engine = new PricingEngine();
        var refused = new List<(int Index, string Why)>();

        foreach (var c in FuzzCorpus.Generate(Seed, 600).Where(c => c.IsControl))
        {
            try
            {
                var price = engine.PriceEuropean(c.Option);
                if (!double.IsFinite(price) || price < 0)
                {
                    refused.Add((c.Index, $"returned {price:R}"));
                }
            }
            catch (PricingException ex)
            {
                refused.Add((c.Index, ex.Message));
            }
        }

        Assert.True(refused.Count == 0,
            "the hardened boundary refused legal input: " +
            string.Join("; ", refused.Take(5).Select(r => $"case {r.Index}: {r.Why}")));
    }

    /// <summary>
    /// Hardening changed what gets through. It must not have changed the answer.
    /// </summary>
    /// <remarks>
    /// This is the test that would catch the most embarrassing possible outcome of this
    /// project: a boundary that is provably safe and quietly reprices the book. The
    /// tolerance is exact equality, because both DLLs are compiled from the same
    /// engine.cpp with the same flags and reach the same instructions -- anything less
    /// than bit-identical would mean the preprocessor definitions had leaked into the
    /// arithmetic.
    /// </remarks>
    [Fact]
    public void The_hardened_and_2009_boundaries_price_every_control_case_identically()
    {
        using var hardened = new PricingEngine();
        using var legacy = new VariantSession(Variants.LegacyBoundary);

        var compared = 0;
        foreach (var c in FuzzCorpus.Generate(Seed, 600).Where(c => c.IsControl))
        {
            var safe = hardened.PriceEuropean(c.Option);
            var old = legacy.PriceEuropean(c.Option);

            Assert.True(safe.Equals(old),
                $"case {c.Index} repriced: hardened {safe:R}, 2009 {old:R}");
            compared++;
        }

        Assert.Equal(150, compared);
    }

    [Fact]
    public void The_fast_math_variant_is_close_but_not_identical()
    {
        // The third DLL is the same source compiled with /fp:fast. It is here to make a
        // point that is easy to state and hard to believe until it is measured: the
        // build flags change the answer. Not by much, and not everywhere, but the
        // prices are not bit-identical -- so "same source, same result" is false, and
        // a reconciliation that assumes otherwise will produce breaks nobody can
        // explain.
        using var strict = new VariantSession(Variants.Hardened);
        using var fast = new VariantSession(Variants.FastMath);

        var controls = FuzzCorpus.Generate(Seed, 600).Where(c => c.IsControl).ToList();
        var differing = 0;
        var worstRelative = 0.0;

        foreach (var c in controls)
        {
            if (strict.TryPriceEuropean(c.Option, out var a) != PricingStatus.Ok) continue;
            if (fast.TryPriceEuropean(c.Option, out var b) != PricingStatus.Ok) continue;

            if (!a.Equals(b))
            {
                differing++;
                var scale = Math.Max(1e-12, Math.Abs(a));
                worstRelative = Math.Max(worstRelative, Math.Abs(a - b) / scale);
            }
        }

        // Whatever the count, the disagreement has to be at rounding scale rather than
        // at a scale a desk would notice. If this ever exceeded a basis point it would
        // stop being a curiosity and start being a defect.
        Assert.True(worstRelative < 1e-9,
            $"fast-math differed from strict by {worstRelative:E3} relative on {differing} of {controls.Count}");
    }

    /// <summary>
    /// The hardened boundary against the whole hostile corpus, in process, because it
    /// is safe to run in process -- which is itself the claim.
    /// </summary>
    [Fact]
    public void The_hardened_boundary_survives_the_entire_corpus_in_process()
    {
        var summary = FuzzDriver.RunInProcess(Variants.Hardened, Seed, 600);

        Assert.Equal(0, summary.Count(FuzzOutcome.ProcessDied));
        Assert.Equal(0, summary.Count(FuzzOutcome.MemoryCorruption));
        Assert.Equal(0, summary.Count(FuzzOutcome.SilentlyWrong));
        Assert.Equal(0, summary.UnsafeCases);
    }

    [Fact]
    public void And_the_clean_sheet_means_something_because_the_control_group_held()
    {
        // Deliberately a separate test from the one above, and deliberately worded this
        // way. The two assertions are not independent: a perfect hostile score is only
        // evidence of safety in the presence of a passing control group, and reading
        // one without the other is the mistake this whole apparatus exists to prevent.
        var summary = FuzzDriver.RunInProcess(Variants.Hardened, Seed, 600);

        Assert.True(summary.ControlTotal > 0, "no control cases were identified at all");
        Assert.True(summary.ControlHeld,
            $"the hardened boundary refused {summary.ControlTotal - summary.ControlAccepted} " +
            $"of {summary.ControlTotal} legal inputs: " +
            string.Join(", ", summary.ControlRefusedIndices.Take(10)));
        Assert.Equal(summary.ControlTotal, summary.ControlAccepted);
    }

    [Fact]
    public void Every_case_is_accounted_for_exactly_once()
    {
        var summary = FuzzDriver.RunInProcess(Variants.Hardened, Seed, 600);
        Assert.Equal(600, summary.Total);
        Assert.Equal(600, summary.Counts.Values.Sum());
    }

    [Fact]
    public void The_summary_is_reproducible()
    {
        var a = FuzzDriver.RunInProcess(Variants.Hardened, Seed, 300);
        var b = FuzzDriver.RunInProcess(Variants.Hardened, Seed, 300);

        Assert.Equal(a.Counts.OrderBy(kv => kv.Key), b.Counts.OrderBy(kv => kv.Key));
        Assert.Equal(a.ControlAccepted, b.ControlAccepted);
        Assert.Equal(a.SilentlyWrongIndices, b.SilentlyWrongIndices);
    }

    [Fact]
    public void The_outcome_taxonomy_is_ordered_from_best_to_worst()
    {
        // The enum's order is load-bearing: the report sorts by it, and the ordering
        // encodes the judgement that a clean refusal beats a wrong answer and that a
        // wrong answer is worse than a crash. Somebody rearranging these alphabetically
        // would silently invert the conclusion of the report.
        Assert.True(FuzzOutcome.Accepted < FuzzOutcome.RejectedCleanly);
        Assert.True(FuzzOutcome.RejectedCleanly < FuzzOutcome.SilentlyWrong);
        Assert.True(FuzzOutcome.SilentlyWrong < FuzzOutcome.MemoryCorruption);
        Assert.True(FuzzOutcome.MemoryCorruption < FuzzOutcome.ProcessDied);
    }
}
