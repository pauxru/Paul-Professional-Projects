using Bridge.Core;

namespace Bridge.Tests;

/// <summary>
/// Checks the closed-form European price against an independently written managed
/// implementation, and against the identities the formula has to satisfy no matter
/// what either implementation does.
/// </summary>
public class EuropeanPricingTests
{
    private const double Tolerance = 1e-9;

    /// <summary>
    /// Hull, <i>Options, Futures and Other Derivatives</i>, the worked European call:
    /// spot 42, strike 40, rate 10%, vol 20%, six months. The published answer is 4.76.
    /// </summary>
    /// <remarks>
    /// This test validates the *oracle*, not the engine. Everything below compares the
    /// C++ engine to <see cref="ReferenceModel"/>, which is worth nothing if the
    /// reference is itself wrong. Anchoring it to a number published in a textbook,
    /// computed by neither implementation, is what closes that loop.
    /// </remarks>
    [Fact]
    public void The_reference_model_reproduces_the_published_textbook_price()
    {
        var price = ReferenceModel.European(42, 40, 0.10, 0.0, 0.20, 0.5, call: true);
        Assert.Equal(4.76, price, 2);
    }

    [Fact]
    public void The_reference_normal_cdf_is_correct_at_the_points_everyone_knows()
    {
        Assert.Equal(0.5, ReferenceModel.NormalCdf(0.0), 15);
        Assert.Equal(0.8413447460685429, ReferenceModel.NormalCdf(1.0), 13);
        Assert.Equal(0.9772498680518208, ReferenceModel.NormalCdf(2.0), 13);
        Assert.Equal(0.15865525393145705, ReferenceModel.NormalCdf(-1.0), 13);
        Assert.Equal(0.0013498980316301, ReferenceModel.NormalCdf(-3.0), 13);
    }

    [Theory]
    // deep in the money, at the money, deep out of the money
    [InlineData(100, 60, 0.05, 0.00, 0.20, 1.0)]
    [InlineData(100, 100, 0.05, 0.00, 0.20, 1.0)]
    [InlineData(100, 160, 0.05, 0.00, 0.20, 1.0)]
    // very short and very long dated
    [InlineData(100, 100, 0.05, 0.00, 0.20, 0.003)]
    [InlineData(100, 100, 0.05, 0.00, 0.20, 30.0)]
    // low and high volatility
    [InlineData(100, 100, 0.05, 0.00, 0.01, 1.0)]
    [InlineData(100, 100, 0.05, 0.00, 2.50, 1.0)]
    // negative and zero rates, which post-2015 are not a pathological case
    [InlineData(100, 100, -0.01, 0.00, 0.20, 1.0)]
    [InlineData(100, 100, 0.00, 0.00, 0.20, 1.0)]
    // dividend yields, including one that exceeds the rate
    [InlineData(100, 100, 0.05, 0.03, 0.20, 1.0)]
    [InlineData(100, 100, 0.02, 0.09, 0.20, 1.0)]
    // penny stocks and index levels, six orders of magnitude apart
    [InlineData(0.05, 0.04, 0.05, 0.00, 0.60, 0.25)]
    [InlineData(38000, 39000, 0.045, 0.017, 0.14, 2.0)]
    public void The_engine_agrees_with_an_independent_implementation(
        double spot, double strike, double rate, double dividend, double vol, double years)
    {
        using var engine = new PricingEngine();

        foreach (var kind in new[] { OptionKind.Call, OptionKind.Put })
        {
            var option = new PricingOption(spot, strike, rate, dividend, vol, years, kind);
            var native = engine.PriceEuropean(option);
            var reference = ReferenceModel.European(
                spot, strike, rate, dividend, vol, years, kind == OptionKind.Call);

            // Relative tolerance: an absolute one would be far too loose on an index
            // priced in the tens of thousands and impossibly tight on a penny stock.
            var scale = Math.Max(1.0, Math.Abs(reference));
            Assert.True(Math.Abs(native - reference) / scale < Tolerance,
                $"{kind} {spot}/{strike}: native {native:R} vs reference {reference:R}");
        }
    }

    [Theory]
    [InlineData(100, 100, 0.05, 0.00, 0.20, 1.0)]
    [InlineData(100, 80, 0.03, 0.02, 0.35, 2.0)]
    [InlineData(50, 70, 0.01, 0.00, 0.15, 0.5)]
    [InlineData(7.5, 7.5, -0.005, 0.04, 0.9, 3.0)]
    public void Put_call_parity_holds(double spot, double strike, double rate,
                                      double dividend, double vol, double years)
    {
        // C - P = S*e^(-qT) - K*e^(-rT). This is an arbitrage identity, not a modelling
        // choice: it has to hold for any correct implementation of any model, which is
        // exactly what makes it a good test. It would catch a sign error in the
        // discounting that a comparison against another Black-Scholes implementation
        // could share and therefore miss.
        using var engine = new PricingEngine();
        var call = engine.PriceEuropean(
            new PricingOption(spot, strike, rate, dividend, vol, years, OptionKind.Call));
        var put = engine.PriceEuropean(
            new PricingOption(spot, strike, rate, dividend, vol, years, OptionKind.Put));

        var expected = spot * Math.Exp(-dividend * years) - strike * Math.Exp(-rate * years);
        Assert.Equal(expected, call - put, 9);
    }

    [Fact]
    public void A_call_is_worth_more_as_the_spot_rises()
    {
        using var engine = new PricingEngine();
        var previous = double.NegativeInfinity;
        for (var spot = 50.0; spot <= 150.0; spot += 2.5)
        {
            var price = engine.PriceEuropean(
                new PricingOption(spot, 100, 0.05, 0.0, 0.2, 1.0, OptionKind.Call));
            Assert.True(price > previous, $"call price fell at spot {spot}");
            previous = price;
        }
    }

    [Fact]
    public void A_put_is_worth_less_as_the_spot_rises()
    {
        using var engine = new PricingEngine();
        var previous = double.PositiveInfinity;
        for (var spot = 50.0; spot <= 150.0; spot += 2.5)
        {
            var price = engine.PriceEuropean(
                new PricingOption(spot, 100, 0.05, 0.0, 0.2, 1.0, OptionKind.Put));
            Assert.True(price < previous, $"put price rose at spot {spot}");
            previous = price;
        }
    }

    [Theory]
    [InlineData(OptionKind.Call)]
    [InlineData(OptionKind.Put)]
    public void Optionality_is_worth_more_as_volatility_rises(OptionKind kind)
    {
        // Vega is positive for both calls and puts. A model that got this backwards for
        // one of them would still produce sensible-looking prices at any single point.
        using var engine = new PricingEngine();
        var previous = double.NegativeInfinity;
        for (var vol = 0.05; vol <= 1.5; vol += 0.05)
        {
            var price = engine.PriceEuropean(
                new PricingOption(100, 100, 0.05, 0.0, vol, 1.0, kind));
            Assert.True(price > previous, $"{kind} price fell at vol {vol}");
            previous = price;
        }
    }

    [Theory]
    [InlineData(OptionKind.Call)]
    [InlineData(OptionKind.Put)]
    public void No_option_is_ever_worth_less_than_nothing(OptionKind kind)
    {
        using var engine = new PricingEngine();
        for (var spot = 1.0; spot <= 400.0; spot += 7.0)
        {
            for (var vol = 0.01; vol <= 1.2; vol += 0.17)
            {
                var price = engine.PriceEuropean(
                    new PricingOption(spot, 100, 0.05, 0.0, vol, 1.0, kind));
                Assert.True(price >= 0, $"{kind} priced at {price} for spot {spot} vol {vol}");
            }
        }
    }

    [Fact]
    public void A_call_never_prices_above_the_discounted_spot()
    {
        // The no-arbitrage upper bound. Breaching it is the signature of a discounting
        // error that survives every point comparison because it is small.
        using var engine = new PricingEngine();
        for (var vol = 0.05; vol <= 3.0; vol += 0.05)
        {
            var price = engine.PriceEuropean(
                new PricingOption(100, 100, 0.05, 0.02, vol, 1.0, OptionKind.Call));
            Assert.True(price <= 100 * Math.Exp(-0.02 * 1.0) + 1e-9,
                $"call priced at {price} above its upper bound at vol {vol}");
        }
    }

    [Fact]
    public void A_put_never_prices_above_the_discounted_strike()
    {
        using var engine = new PricingEngine();
        for (var vol = 0.05; vol <= 3.0; vol += 0.05)
        {
            var price = engine.PriceEuropean(
                new PricingOption(100, 100, 0.05, 0.02, vol, 1.0, OptionKind.Put));
            Assert.True(price <= 100 * Math.Exp(-0.05 * 1.0) + 1e-9,
                $"put priced at {price} above its upper bound at vol {vol}");
        }
    }

    [Fact]
    public void The_same_option_prices_identically_every_time()
    {
        // The closed form has no random component, so bit-identical is the correct
        // standard here -- not "within a tolerance". Anything looser would hide a
        // dependence on uninitialised memory.
        using var engine = new PricingEngine();
        var option = new PricingOption(42, 40, 0.1, 0.0, 0.2, 0.5, OptionKind.Call);
        var first = engine.PriceEuropean(option);
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(first, engine.PriceEuropean(option));
        }
    }

    [Fact]
    public void Two_engines_with_different_seeds_agree_on_a_closed_form_price()
    {
        // The seed drives the Monte Carlo path generator and nothing else. If it
        // reached the analytic path, the closed form would be quietly stochastic.
        using var a = new PricingEngine(1);
        using var b = new PricingEngine(0xDEADBEEF);
        var option = new PricingOption(42, 40, 0.1, 0.0, 0.2, 0.5, OptionKind.Call);
        Assert.Equal(a.PriceEuropean(option), b.PriceEuropean(option));
    }

    [Fact]
    public void Greeks_carry_the_same_price_as_the_price_call()
    {
        using var engine = new PricingEngine();
        var option = new PricingOption(42, 40, 0.1, 0.0, 0.2, 0.5, OptionKind.Call);
        Assert.Equal(engine.PriceEuropean(option), engine.GreeksEuropean(option).Price, 12);
    }

    [Theory]
    [InlineData(OptionKind.Call, 0.0, 1.0)]
    [InlineData(OptionKind.Put, -1.0, 0.0)]
    public void Delta_stays_inside_its_theoretical_range(OptionKind kind, double low, double high)
    {
        using var engine = new PricingEngine();
        for (var spot = 5.0; spot <= 500.0; spot += 5.0)
        {
            var greeks = engine.GreeksEuropean(
                new PricingOption(spot, 100, 0.05, 0.0, 0.2, 1.0, kind));
            Assert.InRange(greeks.Delta, low - 1e-9, high + 1e-9);
        }
    }

    [Fact]
    public void Delta_matches_a_numerical_derivative_of_the_price()
    {
        // The analytic greek and a central difference of the analytic price are two
        // different code paths through the same model. They should agree to about the
        // square root of machine epsilon, which is what a central difference can
        // deliver -- asking for more would be testing floating point, not the engine.
        using var engine = new PricingEngine();
        const double h = 1e-4;

        foreach (var spot in new[] { 60.0, 90.0, 100.0, 110.0, 140.0 })
        {
            var greeks = engine.GreeksEuropean(
                new PricingOption(spot, 100, 0.05, 0.01, 0.2, 1.0, OptionKind.Call));
            var up = engine.PriceEuropean(
                new PricingOption(spot + h, 100, 0.05, 0.01, 0.2, 1.0, OptionKind.Call));
            var down = engine.PriceEuropean(
                new PricingOption(spot - h, 100, 0.05, 0.01, 0.2, 1.0, OptionKind.Call));

            Assert.Equal((up - down) / (2 * h), greeks.Delta, 6);
        }
    }

    [Fact]
    public void Gamma_matches_a_second_numerical_derivative()
    {
        using var engine = new PricingEngine();
        const double h = 1e-2;

        foreach (var spot in new[] { 80.0, 100.0, 120.0 })
        {
            var greeks = engine.GreeksEuropean(
                new PricingOption(spot, 100, 0.05, 0.0, 0.2, 1.0, OptionKind.Call));
            var up = engine.PriceEuropean(
                new PricingOption(spot + h, 100, 0.05, 0.0, 0.2, 1.0, OptionKind.Call));
            var mid = engine.PriceEuropean(
                new PricingOption(spot, 100, 0.05, 0.0, 0.2, 1.0, OptionKind.Call));
            var down = engine.PriceEuropean(
                new PricingOption(spot - h, 100, 0.05, 0.0, 0.2, 1.0, OptionKind.Call));

            Assert.Equal((up - 2 * mid + down) / (h * h), greeks.Gamma, 4);
        }
    }

    [Fact]
    public void Gamma_and_vega_are_the_same_for_a_call_and_a_put()
    {
        // Both follow from put-call parity: the parity difference is linear in spot and
        // independent of volatility, so its second spot derivative and its vol
        // derivative are both zero.
        using var engine = new PricingEngine();
        for (var spot = 60.0; spot <= 140.0; spot += 10.0)
        {
            var call = engine.GreeksEuropean(
                new PricingOption(spot, 100, 0.05, 0.02, 0.25, 1.0, OptionKind.Call));
            var put = engine.GreeksEuropean(
                new PricingOption(spot, 100, 0.05, 0.02, 0.25, 1.0, OptionKind.Put));

            Assert.Equal(call.Gamma, put.Gamma, 12);
            Assert.Equal(call.Vega, put.Vega, 10);
        }
    }

    [Fact]
    public void Call_delta_minus_put_delta_is_the_dividend_discount_factor()
    {
        // Differentiating parity once with respect to spot. Another identity that has
        // to hold regardless of model.
        using var engine = new PricingEngine();
        for (var spot = 70.0; spot <= 130.0; spot += 10.0)
        {
            var call = engine.GreeksEuropean(
                new PricingOption(spot, 100, 0.05, 0.03, 0.25, 2.0, OptionKind.Call));
            var put = engine.GreeksEuropean(
                new PricingOption(spot, 100, 0.05, 0.03, 0.25, 2.0, OptionKind.Put));

            Assert.Equal(Math.Exp(-0.03 * 2.0), call.Delta - put.Delta, 10);
        }
    }
}
