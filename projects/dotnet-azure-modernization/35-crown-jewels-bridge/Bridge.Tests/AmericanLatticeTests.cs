using Bridge.Core;
using Bridge.Legacy;

namespace Bridge.Tests;

/// <summary>
/// The Cox-Ross-Rubinstein lattice, and the reason its convergence cannot be tested
/// the obvious way.
/// </summary>
public class AmericanLatticeTests
{
    private static readonly PricingOption AmericanPut =
        new(42, 40, 0.10, 0.0, 0.20, 0.5, OptionKind.Put);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(101)]
    [InlineData(512)]
    public void The_lattice_agrees_with_an_independent_implementation(int steps)
    {
        // Differential test against a managed CRR written from the recurrence rather
        // than transcribed from the C++. Exact agreement to twelve places means both
        // implementations chose the same up factor, the same risk-neutral probability
        // and the same discounting -- which are the three places CRR implementations
        // usually differ from each other.
        using var engine = new PricingEngine();
        var native = engine.PriceAmerican(AmericanPut, steps);
        var reference = ReferenceModel.Crr(42, 40, 0.10, 0.0, 0.20, 0.5,
            call: false, american: true, steps: steps);

        Assert.Equal(reference, native, 12);
    }

    [Fact]
    public void An_american_call_on_a_non_dividend_payer_equals_the_european_call()
    {
        // Merton's result: early exercise of an American call is never optimal without
        // dividends. The lattice has to rediscover this on its own -- it checks the
        // exercise condition at every node and must find it never binds. Agreement
        // with the closed form to five places at 2000 steps is the lattice confirming
        // a theorem it does not know about.
        using var engine = new PricingEngine();
        var option = new PricingOption(42, 40, 0.10, 0.0, 0.20, 0.5, OptionKind.Call);

        var american = engine.PriceAmerican(option, 2000);
        var european = engine.PriceEuropean(option);

        // An explicit tolerance, not Assert.Equal's decimal-places overload. That
        // overload rounds both values before comparing, so two numbers 1.2e-4 apart
        // land on opposite sides of a rounding boundary and are reported as differing
        // in the third decimal when they agree to within a ten-thousandth. CRR error
        // falls as O(1/steps), so 2000 steps buys about 1e-4 and no amount of wishing
        // buys more.
        Assert.True(Math.Abs(european - american) < 5e-4,
            $"american {american:R} and european {european:R} differ by {european - american:R}");
    }

    [Fact]
    public void An_american_put_is_worth_at_least_the_european_put()
    {
        // Early exercise of a put can be optimal, so the extra right has non-negative
        // value. For a put this deep in the money the premium is real, not noise.
        using var engine = new PricingEngine();
        var american = engine.PriceAmerican(
            new PricingOption(30, 40, 0.10, 0.0, 0.20, 1.0, OptionKind.Put), 2000);
        var european = engine.PriceEuropean(
            new PricingOption(30, 40, 0.10, 0.0, 0.20, 1.0, OptionKind.Put));

        Assert.True(american > european,
            $"american put {american} should exceed european put {european}");
    }

    [Fact]
    public void An_american_option_is_never_worth_less_than_immediate_exercise()
    {
        using var engine = new PricingEngine();
        for (var spot = 10.0; spot <= 40.0; spot += 2.0)
        {
            var price = engine.PriceAmerican(
                new PricingOption(spot, 40, 0.10, 0.0, 0.20, 1.0, OptionKind.Put), 400);
            Assert.True(price >= 40.0 - spot - 1e-9,
                $"american put at spot {spot} priced {price}, below its intrinsic {40.0 - spot}");
        }
    }

    /// <summary>
    /// The convergence test that has to be written as a Cauchy test, not a comparison
    /// against a known answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The obvious test is "price at 2000 steps and check it is close to the published
    /// value". That test is wrong twice over. It bakes in a constant that has to be
    /// obtained from somewhere, and the value quoted in the literature for this
    /// particular option is 4.478, which this lattice does not converge to -- it
    /// converges to about 4.487. Chasing that discrepancy is how this test ended up
    /// being written properly.
    /// </para>
    /// <para>
    /// The second problem is that CRR error <b>oscillates</b>. The price at an even
    /// number of steps and at an odd number of steps approach the true value from
    /// opposite sides, because whether a lattice node lands exactly on the strike
    /// depends on the parity of the step count. So the error is not monotone in steps,
    /// and any test asserting that it is will fail on a step count that happens to
    /// land on the wrong parity.
    /// </para>
    /// <para>
    /// Fixing the parity is <i>still</i> not enough, which is the part that cost the
    /// most time. Holding every step count even and doubling the resolution gives gaps
    /// of 1.02e-3, 6.70e-4, <b>6.93e-4</b>, 4.48e-5, 6.20e-5 -- decaying overall, but
    /// with the third value larger than the second. The reason is that the quantity
    /// that actually oscillates is not <c>N mod 2</c> but how close the nearest lattice
    /// node sits to the strike, and that distance wanders as the node spacing
    /// <c>sigma*sqrt(T/N)</c> changes. Parity is a proxy for it, not the thing itself.
    /// </para>
    /// <para>
    /// So the honest statements are the two that survive the wobble: the gap stays
    /// under an O(1/N) envelope, and it shrinks across <i>two</i> doublings even where
    /// it grows across one. Both are testable without knowing the answer, which is the
    /// entire point -- there is no trustworthy published constant for this option, so a
    /// test that needs one cannot be written.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_lattice_converges_in_the_cauchy_sense_within_a_parity_class()
    {
        using var engine = new PricingEngine();

        // All even, so the crudest source of oscillation is held fixed.
        var counts = new[] { 100, 200, 400, 800, 1600 };
        var gaps = new double[counts.Length];

        for (var i = 0; i < counts.Length; i++)
        {
            var coarse = engine.PriceAmerican(AmericanPut, counts[i]);
            var fine = engine.PriceAmerican(AmericanPut, counts[i] * 2);
            gaps[i] = Math.Abs(fine - coarse);
        }

        // CRR converges at O(1/N). The envelope is generous enough to survive the node
        // wobble and tight enough that a genuinely broken lattice -- one converging at
        // O(1/sqrt(N)), or not at all -- would break it at the far end.
        for (var i = 0; i < counts.Length; i++)
        {
            Assert.True(gaps[i] < 0.5 / counts[i],
                $"gap at {counts[i]} steps was {gaps[i]:E3}, outside the O(1/N) envelope");
        }

        // Local growth is expected; growth sustained over two doublings is not.
        for (var i = 2; i < gaps.Length; i++)
        {
            Assert.True(gaps[i] < gaps[i - 2],
                $"gap at {counts[i]} steps ({gaps[i]:E3}) is no better than " +
                $"at {counts[i - 2]} ({gaps[i - 2]:E3}); this is not converging");
        }

        Assert.True(gaps[^1] < 1e-3,
            $"still {gaps[^1]:E3} apart after 3200 steps; that is not convergence");
    }

    [Fact]
    public void Error_oscillates_with_step_parity_which_is_why_the_naive_test_fails()
    {
        // Demonstrates the trap directly. Consecutive step counts straddle the limit,
        // so the sequence 200, 201, 202, ... is not monotone and never will be.
        using var engine = new PricingEngine();
        var limit = engine.PriceAmerican(AmericanPut, 6000);

        var evenErrors = new List<double>();
        var oddErrors = new List<double>();
        for (var steps = 200; steps < 220; steps++)
        {
            var error = engine.PriceAmerican(AmericanPut, steps) - limit;
            (steps % 2 == 0 ? evenErrors : oddErrors).Add(error);
        }

        // Each parity class is consistently on one side; the two classes are on
        // opposite sides. That is the oscillation, stated as a testable fact.
        Assert.True(evenErrors.TrueForAll(e => e > 0) || evenErrors.TrueForAll(e => e < 0),
            "even-step errors changed sign; the oscillation model is wrong");
        Assert.True(oddErrors.TrueForAll(e => e > 0) || oddErrors.TrueForAll(e => e < 0),
            "odd-step errors changed sign; the oscillation model is wrong");
        Assert.True(Math.Sign(evenErrors[0]) != Math.Sign(oddErrors[0]),
            "even and odd steps approached from the same side");
    }

    [Fact]
    public void A_single_step_lattice_is_still_a_price()
    {
        // Wildly inaccurate, entirely well-defined. The boundary must not confuse
        // "imprecise" with "invalid": one step is a legal request.
        using var engine = new PricingEngine();
        var price = engine.PriceAmerican(AmericanPut, 1);
        Assert.True(double.IsFinite(price) && price >= 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_non_positive_step_count_is_refused(int steps)
    {
        using var engine = new PricingEngine();
        var ex = Assert.Throws<PricingException>(() => engine.PriceAmerican(AmericanPut, steps));
        Assert.Equal(PricingStatus.BadArgument, ex.Status);
        Assert.Contains("steps", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_step_count_that_would_take_the_machine_hostage_is_refused()
    {
        // Not a memory-safety problem. The lattice is O(steps^2), so two billion steps
        // is a request to occupy a core until somebody kills the process. That is a
        // capacity answer rather than a correctness one, and the boundary is the only
        // place that can give it -- the engine has no notion of how long is too long.
        using var engine = new PricingEngine();
        var ex = Assert.Throws<PricingException>(
            () => engine.PriceAmerican(AmericanPut, int.MaxValue));
        Assert.Equal(PricingStatus.BadArgument, ex.Status);
    }

    [Fact]
    public void The_2009_boundary_has_no_step_count_check_to_reach()
    {
        // There is no in-process version of this test. Both an invalid step count and
        // a zero one take the whole host with them against the legacy DLL -- which was
        // discovered by writing the in-process version first and watching the run abort
        // at test nine of a hundred and forty. The claim is asserted from a child
        // process in LegacyCrashTests instead.
        //
        // What can be checked here is that the hardened boundary has the check the
        // legacy one lacks, and that its refusal is specific enough to act on.
        using var engine = new PricingEngine();
        foreach (var steps in new[] { -1, 0 })
        {
            var ex = Assert.Throws<PricingException>(() => engine.PriceAmerican(AmericanPut, steps));
            Assert.Contains("steps", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_same_lattice_price_comes_back_every_time()
    {
        using var engine = new PricingEngine();
        var first = engine.PriceAmerican(AmericanPut, 500);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(first, engine.PriceAmerican(AmericanPut, 500));
        }
    }
}
