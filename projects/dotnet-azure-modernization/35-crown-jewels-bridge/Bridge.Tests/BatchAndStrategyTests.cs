using Bridge.Core;

namespace Bridge.Tests;

/// <summary>
/// Batch pricing and the four marshalling strategies, which have to agree with each
/// other to the last bit or the benchmark comparing them is meaningless.
/// </summary>
public class BatchAndStrategyTests
{
    private static PricingOption[] Book(int count)
    {
        var options = new PricingOption[count];
        for (var i = 0; i < count; i++)
        {
            options[i] = new PricingOption(
                spot: 80 + (i % 40),
                strike: 100,
                rate: 0.05,
                dividend: 0.01,
                volatility: 0.15 + (i % 7) * 0.05,
                years: 0.25 + (i % 5) * 0.5,
                kind: i % 2 == 0 ? OptionKind.Call : OptionKind.Put);
        }
        return options;
    }

    [Fact]
    public void A_batch_returns_exactly_what_pricing_each_option_alone_returns()
    {
        // Bit-identical, not approximately equal. The batch entry point exists to
        // amortise one boundary crossing over many options, not to compute anything
        // differently. If these ever diverge, the batch path has picked up its own
        // arithmetic and the two answers on a risk report will not tie out.
        using var engine = new PricingEngine();
        var options = Book(500);

        var batch = engine.PriceBatch(options);
        for (var i = 0; i < options.Length; i++)
        {
            Assert.Equal(engine.PriceEuropean(options[i]), batch[i]);
        }
    }

    [Fact]
    public void An_empty_batch_is_a_legal_request()
    {
        // An empty book is what a desk with no positions looks like on a quiet
        // morning. It is not an error, and a boundary that treats it as one produces
        // an alert every day at the same time until somebody suppresses the alert.
        using var engine = new PricingEngine();
        var prices = engine.PriceBatch(Array.Empty<PricingOption>());
        Assert.Empty(prices);
    }

    [Fact]
    public void A_batch_of_one_works()
    {
        using var engine = new PricingEngine();
        var option = new PricingOption(42, 40, 0.1, 0.0, 0.2, 0.5, OptionKind.Call);
        var prices = engine.PriceBatch([option]);
        Assert.Equal(engine.PriceEuropean(option), Assert.Single(prices));
    }

    [Fact]
    public void An_output_buffer_smaller_than_the_batch_is_refused_before_the_call()
    {
        // The managed side refuses this without ever reaching native code, which is
        // the correct place for it: by the time the C function has a short buffer it
        // can only decline, whereas here the caller gets an ArgumentException naming
        // both lengths.
        using var engine = new PricingEngine();
        var options = Book(10);
        var tooSmall = new double[9];

        var ex = Assert.Throws<ArgumentException>(() => engine.PriceBatch(options, tooSmall));
        Assert.Contains("9", ex.Message);
        Assert.Contains("10", ex.Message);
    }

    [Fact]
    public void A_larger_output_buffer_is_fine_and_leaves_the_tail_untouched()
    {
        using var engine = new PricingEngine();
        var options = Book(4);
        var buffer = new double[10];
        Array.Fill(buffer, -999.0);

        engine.PriceBatch(options, buffer);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(engine.PriceEuropean(options[i]), buffer[i]);
        }
        for (var i = 4; i < 10; i++)
        {
            Assert.Equal(-999.0, buffer[i]);
        }
    }

    [Fact]
    public void One_bad_option_fails_the_whole_batch_and_names_its_index()
    {
        // Not a partial result. A caller who receives 499 good prices and one NaN has
        // no way to tell which is which after the array leaves the call site, and will
        // book all five hundred.
        using var engine = new PricingEngine();
        var options = Book(500);
        options[317] = options[317] with { Volatility = -1.0 };

        var ex = Assert.Throws<PricingException>(() => engine.PriceBatch(options));
        Assert.Equal(PricingStatus.BadArgument, ex.Status);
        Assert.Contains("317", ex.Message);
        Assert.Contains("volatility", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_failed_batch_writes_nothing_the_caller_could_mistake_for_a_price()
    {
        using var engine = new PricingEngine();
        var options = Book(20);
        options[5] = options[5] with { Spot = double.NaN };
        var buffer = new double[20];
        Array.Fill(buffer, -1.0);

        Assert.Throws<PricingException>(() => engine.PriceBatch(options, buffer));

        // The sentinel is still there: validation happens before any price is written.
        Assert.All(buffer, v => Assert.Equal(-1.0, v));
    }

    [Fact]
    public void A_batch_containing_an_option_at_expiry_prices_it_the_same_way_a_single_call_does()
    {
        // The degenerate-input limit is taken at the boundary, so it has to be taken on
        // both paths. Handling it in the single-price entry point and forgetting the
        // batch would put a NaN into exactly one of two code paths that are supposed to
        // be interchangeable -- and the batch path is the one production uses.
        using var engine = new PricingEngine();
        var options = new[]
        {
            new PricingOption(42, 40, 0.1, 0.0, 0.2, 0.5, OptionKind.Call),
            new PricingOption(40, 40, 0.1, 0.0, 0.2, 0.0, OptionKind.Call),
            new PricingOption(42, 40, 0.1, 0.0, 0.0, 0.5, OptionKind.Call),
        };

        var batch = engine.PriceBatch(options);

        Assert.All(batch, p => Assert.True(double.IsFinite(p)));
        for (var i = 0; i < options.Length; i++)
        {
            Assert.Equal(engine.PriceEuropean(options[i]), batch[i]);
        }
    }

    [Fact]
    public void Every_marshalling_strategy_produces_bit_identical_prices()
    {
        // Four ways of getting the same bytes to the same function: per-call pinning,
        // per-call with the GC transition suppressed, one pinned batch, and a copy to
        // unmanaged memory. They cost very different amounts, which is the point of
        // having them, and they must not compute different answers, which is the point
        // of this test. A benchmark comparing strategies that disagree is measuring
        // nothing.
        var options = Book(1000);
        var reference = new double[options.Length];

        using (var engine = new PricingEngine())
        {
            engine.PriceBatch(options, reference);
        }

        foreach (var strategy in BlittableStrategies.All)
        {
            using var engine = new PricingEngine();
            var prices = new double[options.Length];
            strategy.Price(GetRawHandle(engine), options, prices);

            for (var i = 0; i < options.Length; i++)
            {
                Assert.True(reference[i].Equals(prices[i]),
                    $"{strategy.Name} disagreed at index {i}: {prices[i]:R} vs {reference[i]:R}");
            }
        }
    }

    [Fact]
    public void Every_strategy_reports_a_name_a_report_can_print()
    {
        Assert.All(BlittableStrategies.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
        var names = BlittableStrategies.All.Select(s => s.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    /// <summary>
    /// Reaches the raw engine pointer the way the benchmark does.
    /// </summary>
    /// <remarks>
    /// The strategies take an <c>nint</c> because they exist to measure the cost of
    /// the crossing itself, with nothing between the call site and the function. That
    /// is exactly the shape a consumer must never write, which is why it lives behind
    /// InternalsVisibleTo and appears in precisely two assemblies.
    /// </remarks>
    private static nint GetRawHandle(PricingEngine engine)
    {
        var field = typeof(PricingEngine).GetField("_handle",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var handle = (EngineHandle)field!.GetValue(engine)!;
        return handle.DangerousGetHandle();
    }
}
