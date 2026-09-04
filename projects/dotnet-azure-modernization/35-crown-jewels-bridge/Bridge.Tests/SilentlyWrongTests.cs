using Bridge.Core;
using Bridge.Legacy;

namespace Bridge.Tests;

/// <summary>
/// The central claim of this project, as a test: the dangerous outputs of a numerical
/// library are not the ones that crash. They are the ones that are finite, plausible,
/// and wrong.
/// </summary>
/// <remarks>
/// <para>
/// Every case below produces a number that would pass a range check, survive
/// serialisation, and appear on a risk report without anybody noticing. The hardened
/// boundary refuses all of them. The 2009 boundary -- compiled from byte-identical
/// C++ source, differing only in preprocessor definitions -- returns them.
/// </para>
/// <para>
/// That is the whole argument for spending the money on the boundary rather than the
/// rewrite. The mathematics was never the problem.
/// </para>
/// </remarks>
public class SilentlyWrongTests
{
    private static readonly PricingOption Sane =
        new(42, 40, 0.10, 0.0, 0.20, 0.5, OptionKind.Call);

    // ------------------------------------------------------------ negative volatility

    /// <summary>
    /// A negative volatility does not blow up. It prices a different instrument.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sigma appears in d1 only as sigma-squared in the numerator and sigma in the
    /// denominator, so flipping its sign flips d1 and d2 and nothing else:
    /// </para>
    /// <code>
    /// d1(-s) = (ln(S/K) + (r-q+s^2/2)T) / (-s*sqrt(T)) = -d1(s)
    /// d2(-s) = d1(-s) + s*sqrt(T)                      = -d2(s)
    /// </code>
    /// <para>
    /// Substituting into the call formula gives exactly minus the put formula. So a
    /// sign error in a volatility feed -- a subtraction the wrong way round in a
    /// surface interpolation, a spreadsheet column with a stray minus -- does not
    /// produce an error. It produces the negated price of the opposite instrument,
    /// which for an out-of-the-money option is a small negative number that looks like
    /// a rounding artefact and gets booked.
    /// </para>
    /// <para>
    /// This test proves the identity holds exactly, using the 2009 boundary because
    /// the hardened one refuses the input. That refusal is the entire value of the
    /// hardening, and it is measured in the test below this one.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(100, 100, 0.05, 0.00, 0.20, 1.0)]
    [InlineData(42, 40, 0.10, 0.00, 0.20, 0.5)]
    [InlineData(100, 150, 0.03, 0.02, 0.45, 2.0)]
    [InlineData(80, 60, 0.01, 0.00, 0.15, 0.25)]
    public void Negating_volatility_silently_prices_the_opposite_instrument(
        double spot, double strike, double rate, double dividend, double vol, double years)
    {
        using var legacy = new VariantSession(Variants.LegacyBoundary);

        var callAtNegativeVol = legacy.PriceEuropean(
            new PricingOption(spot, strike, rate, dividend, -vol, years, OptionKind.Call));
        var putAtPositiveVol = legacy.PriceEuropean(
            new PricingOption(spot, strike, rate, dividend, vol, years, OptionKind.Put));

        Assert.Equal(-putAtPositiveVol, callAtNegativeVol, 12);

        // And symmetrically, in case anyone thinks this is special to calls.
        var putAtNegativeVol = legacy.PriceEuropean(
            new PricingOption(spot, strike, rate, dividend, -vol, years, OptionKind.Put));
        var callAtPositiveVol = legacy.PriceEuropean(
            new PricingOption(spot, strike, rate, dividend, vol, years, OptionKind.Call));

        Assert.Equal(-callAtPositiveVol, putAtNegativeVol, 12);
    }

    [Fact]
    public void The_negated_price_is_finite_which_is_precisely_the_problem()
    {
        // If it were NaN or infinite, every downstream consumer would notice. It is a
        // perfectly ordinary double, and small, so it survives every sanity check that
        // is not specifically looking for a negative premium.
        using var legacy = new VariantSession(Variants.LegacyBoundary);
        var price = legacy.PriceEuropean(Sane with { Volatility = -0.20 });
        Assert.True(double.IsFinite(price));
        Assert.True(Math.Abs(price) < 1.0, $"expected a small, plausible number, got {price}");
    }

    [Theory]
    [InlineData(-0.20)]
    [InlineData(-1e-12)]
    [InlineData(-1e6)]
    public void The_hardened_boundary_refuses_a_negative_volatility(double vol)
    {
        using var engine = new PricingEngine();
        var ex = Assert.Throws<PricingException>(
            () => engine.PriceEuropean(Sane with { Volatility = vol }));
        Assert.Equal(PricingStatus.BadArgument, ex.Status);
    }

    // --------------------------------------------------------------- degenerate input

    [Fact]
    public void Zero_volatility_prices_the_discounted_intrinsic_value()
    {
        // Not an error: with no volatility the payoff is deterministic, and the
        // forward-intrinsic discounted back is the right answer. The engine has to
        // reach it by a limit the closed form cannot take directly -- d1 and d2 both
        // go to infinity with a sign that depends on moneyness.
        using var engine = new PricingEngine();
        var price = engine.PriceEuropean(Sane with { Volatility = 0.0 });

        var forward = 42.0 * Math.Exp(0.10 * 0.5);
        var expected = Math.Exp(-0.10 * 0.5) * Math.Max(forward - 40.0, 0.0);
        Assert.Equal(expected, price, 9);
    }

    [Fact]
    public void Zero_volatility_out_of_the_money_is_worth_nothing()
    {
        using var engine = new PricingEngine();
        var price = engine.PriceEuropean(
            new PricingOption(42, 400, 0.10, 0.0, 0.0, 0.5, OptionKind.Call));
        Assert.Equal(0.0, price, 12);
    }

    [Fact]
    public void An_expired_option_is_worth_its_intrinsic_value()
    {
        using var engine = new PricingEngine();

        Assert.Equal(2.0, engine.PriceEuropean(
            new PricingOption(42, 40, 0.1, 0.0, 0.2, 0.0, OptionKind.Call)), 12);
        Assert.Equal(0.0, engine.PriceEuropean(
            new PricingOption(42, 44, 0.1, 0.0, 0.2, 0.0, OptionKind.Call)), 12);
        Assert.Equal(2.0, engine.PriceEuropean(
            new PricingOption(42, 44, 0.1, 0.0, 0.2, 0.0, OptionKind.Put)), 12);
    }

    [Fact]
    public void An_option_expiring_exactly_at_the_money_is_the_case_that_produces_a_nan()
    {
        // T = 0 and S = K puts 0/0 in d1's numerator and denominator simultaneously.
        // This is the single most common real-world path to a NaN in a pricing library,
        // because it happens on every expiry date to every at-the-money strike, and it
        // is reached by perfectly ordinary market data rather than by anything hostile.
        //
        // The hardened boundary does not refuse it. Refusing would be a false positive
        // on the most routine input in the book. It takes the limit at the seam and
        // returns the right answer: an at-the-money option at expiry is worth exactly
        // nothing.
        using var engine = new PricingEngine();
        var price = engine.PriceEuropean(
            new PricingOption(40, 40, 0.1, 0.0, 0.2, 0.0, OptionKind.Call));
        Assert.Equal(0.0, price);
    }

    [Fact]
    public void Greeks_at_expiry_are_refused_because_they_do_not_exist()
    {
        // The price has a limit at expiry. Theta does not -- it is unbounded -- and
        // gamma at zero volatility is a delta function. Returning a number for either
        // would be inventing one, so the boundary declines and says why. This is the
        // distinction the whole project is about: refusing an answer that does not
        // exist is safety; refusing one that does is just breakage.
        using var engine = new PricingEngine();
        var ex = Assert.Throws<PricingException>(() => engine.GreeksEuropean(
            new PricingOption(40, 40, 0.1, 0.0, 0.2, 0.0, OptionKind.Call)));
        Assert.Equal(PricingStatus.BadArgument, ex.Status);
        Assert.Contains("undefined", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_2009_boundary_returns_that_nan_as_a_successful_price()
    {
        using var legacy = new VariantSession(Variants.LegacyBoundary);
        var status = legacy.TryPriceEuropean(
            new PricingOption(40, 40, 0.1, 0.0, 0.2, 0.0, OptionKind.Call), out var price);

        // The status is the point. It is not an error code -- it is success, carrying
        // a NaN. Every caller that checks the status and then uses the number is
        // behaving correctly and getting a NaN into its book.
        Assert.Equal(PricingStatus.Ok, status);
        Assert.True(double.IsNaN(price));
    }

    [Fact]
    public void An_absurd_interest_rate_saturates_at_the_spot_rather_than_overflowing()
    {
        // exp(-r*T) underflows to zero and the discounted strike vanishes, leaving the
        // spot. The answer is arithmetically correct and financially meaningless: a
        // call struck at 40 is not worth 42 because the rate is ten million percent.
        // Nothing overflows, so nothing complains.
        using var legacy = new VariantSession(Variants.LegacyBoundary);
        var price = legacy.PriceEuropean(Sane with { Rate = 1e5 });
        Assert.Equal(42.0, price, 9);
    }

    [Fact]
    public void The_hardened_boundary_refuses_an_absurd_interest_rate()
    {
        using var engine = new PricingEngine();
        var ex = Assert.Throws<PricingException>(() => engine.PriceEuropean(Sane with { Rate = 1e5 }));
        Assert.Equal(PricingStatus.BadArgument, ex.Status);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Non_finite_input_is_refused_in_every_field(double poison)
    {
        using var engine = new PricingEngine();

        foreach (var option in new[]
        {
            Sane with { Spot = poison },
            Sane with { Strike = poison },
            Sane with { Rate = poison },
            Sane with { Dividend = poison },
            Sane with { Volatility = poison },
            Sane with { Years = poison },
        })
        {
            var ex = Assert.Throws<PricingException>(() => engine.PriceEuropean(option));
            Assert.Equal(PricingStatus.BadArgument, ex.Status);
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(-1e-300)]
    public void A_non_positive_spot_is_refused(double spot)
    {
        // log(S/K) is negative infinity at zero and NaN below it. Neither is a price.
        using var engine = new PricingEngine();
        Assert.Throws<PricingException>(() => engine.PriceEuropean(Sane with { Spot = spot }));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-40.0)]
    public void A_non_positive_strike_is_refused(double strike)
    {
        using var engine = new PricingEngine();
        Assert.Throws<PricingException>(() => engine.PriceEuropean(Sane with { Strike = strike }));
    }

    [Fact]
    public void A_negative_time_to_expiry_is_refused()
    {
        using var engine = new PricingEngine();
        Assert.Throws<PricingException>(() => engine.PriceEuropean(Sane with { Years = -0.5 }));
    }

    [Fact]
    public void An_option_that_expires_after_the_heat_death_of_the_sun_is_refused()
    {
        using var engine = new PricingEngine();
        Assert.Throws<PricingException>(() => engine.PriceEuropean(Sane with { Years = 1e9 }));
    }

    [Fact]
    public void A_strike_at_the_edge_of_the_double_range_is_refused()
    {
        // 1e300 does not overflow. It produces a call worth exactly zero, which is
        // arithmetically defensible and operationally a lie: the input was corrupt and
        // the answer looks like a legitimately worthless option. Nothing about the
        // strike on its own is wrong -- it is finite and positive -- so the check that
        // catches it has to be on the relationship between the strike and the spot.
        using var engine = new PricingEngine();
        var ex = Assert.Throws<PricingException>(() => engine.PriceEuropean(Sane with { Strike = 1e300 }));
        Assert.Contains("relative to spot", ex.Message);
    }

    [Fact]
    public void The_2009_boundary_prices_that_corrupt_strike_at_zero()
    {
        using var legacy = new VariantSession(Variants.LegacyBoundary);
        var status = legacy.TryPriceEuropean(Sane with { Strike = 1e300 }, out var price);
        Assert.Equal(PricingStatus.Ok, status);
        Assert.Equal(0.0, price);
    }

    [Fact]
    public void A_moneyness_ratio_the_market_could_actually_produce_is_still_priced()
    {
        // The relative check has to be loose enough not to become the false positive it
        // was added to prevent. A strike a million times the spot is absurd but legal;
        // it prices, and it prices at zero because that is what it is worth.
        using var engine = new PricingEngine();
        var price = engine.PriceEuropean(Sane with { Strike = 42e6 });
        Assert.Equal(0.0, price, 12);
    }

    [Fact]
    public void Every_hardened_refusal_names_the_field_that_was_wrong()
    {
        // A boundary that rejects cleanly but says only "bad argument" moves the
        // problem from the pricing library to the support queue. Each message has to
        // be specific enough to act on.
        using var engine = new PricingEngine();

        var cases = new (PricingOption Option, string Expect)[]
        {
            (Sane with { Volatility = -1 }, "volatility"),
            (Sane with { Spot = 0 }, "spot"),
            (Sane with { Strike = -1 }, "strike"),
            (Sane with { Years = -1 }, "years"),
            (Sane with { Rate = 1e5 }, "rate"),
        };

        foreach (var (option, expect) in cases)
        {
            Assert.Throws<PricingException>(() => engine.PriceEuropean(option));
            Assert.Contains(expect, engine.LastError(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
