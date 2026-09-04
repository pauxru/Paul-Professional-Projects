using Bridge.Core;

namespace Bridge.Tests;

/// <summary>
/// The reverse direction of the boundary: native code calling back into managed code,
/// which is where the genuinely subtle failures live.
/// </summary>
public class MonteCarloTests
{
    private static readonly PricingOption Sane =
        new(42, 40, 0.10, 0.0, 0.20, 0.5, OptionKind.Call);

    [Fact]
    public void Monte_carlo_converges_to_the_closed_form()
    {
        // Antithetic variates, so the standard error falls faster than 1/sqrt(n), but
        // this is still a statistical test and the tolerance has to admit that. Two
        // basis points on a four-dollar option at a million paths is comfortable
        // without being so loose that a genuinely broken estimator would pass.
        using var engine = new PricingEngine();
        var mc = engine.PriceMonteCarlo(Sane, 1_000_000);
        var analytic = engine.PriceEuropean(Sane);

        Assert.Equal(analytic, mc, 2);
    }

    [Fact]
    public void The_same_seed_gives_the_same_answer()
    {
        // Reproducibility is not a nicety here. A price that cannot be reproduced
        // cannot be explained to a regulator, and "the random numbers were different"
        // is not an explanation anyone accepts.
        using var a = new PricingEngine(12345);
        using var b = new PricingEngine(12345);
        Assert.Equal(a.PriceMonteCarlo(Sane, 200_000), b.PriceMonteCarlo(Sane, 200_000));
    }

    [Fact]
    public void A_different_seed_gives_a_different_answer()
    {
        // The complement of the test above, and the one that would catch a seed that
        // is accepted and then ignored -- which would make every run reproducible for
        // entirely the wrong reason.
        using var a = new PricingEngine(1);
        using var b = new PricingEngine(2);
        Assert.NotEqual(a.PriceMonteCarlo(Sane, 200_000), b.PriceMonteCarlo(Sane, 200_000));
    }

    [Fact]
    public void Progress_is_reported_as_often_as_asked_and_no_more()
    {
        using var engine = new PricingEngine();
        var calls = 0;
        engine.PriceMonteCarlo(Sane, 100_000, reportEvery: 10_000, progress: (_, _) =>
        {
            calls++;
            return true;
        });

        Assert.True(calls > 0, "no progress was reported at all");
        Assert.Equal(calls, engine.LastProgressCallCount);
    }

    [Fact]
    public void Asking_for_no_progress_produces_no_callbacks()
    {
        using var engine = new PricingEngine();
        var calls = 0;
        engine.PriceMonteCarlo(Sane, 100_000, reportEvery: 0, progress: (_, _) =>
        {
            calls++;
            return true;
        });

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Progress_counts_only_ever_increase()
    {
        using var engine = new PricingEngine();
        long previous = -1;
        engine.PriceMonteCarlo(Sane, 500_000, reportEvery: 25_000, progress: (done, total) =>
        {
            Assert.True(done > previous, $"progress went backwards: {done} after {previous}");
            Assert.True(done <= total, $"progress {done} exceeded total {total}");
            previous = done;
            return true;
        });

        Assert.True(previous > 0);
    }

    [Fact]
    public void Returning_false_from_the_callback_stops_the_run()
    {
        using var engine = new PricingEngine();
        var calls = 0;

        Assert.Throws<OperationCanceledException>(() =>
            engine.PriceMonteCarlo(Sane, 100_000_000, reportEvery: 1_000, progress: (_, _) =>
            {
                calls++;
                return calls < 3;
            }));

        // Stopped almost immediately rather than after a hundred million paths.
        Assert.Equal(3, calls);
    }

    [Fact]
    public void A_cancellation_token_stops_the_run_and_throws_the_right_exception()
    {
        using var engine = new PricingEngine();
        using var cts = new CancellationTokenSource();

        var ex = Assert.Throws<OperationCanceledException>(() =>
            engine.PriceMonteCarlo(Sane, 100_000_000, reportEvery: 1_000,
                progress: (_, _) => { cts.Cancel(); return true; },
                cancellationToken: cts.Token));

        // Specifically an OperationCanceledException carrying the token, not a generic
        // one: callers distinguish "I cancelled this" from "something stopped".
        Assert.Equal(cts.Token, Assert.IsType<OperationCanceledException>(ex).CancellationToken);
    }

    /// <summary>
    /// A managed exception thrown inside a callback must never unwind through a native
    /// frame.
    /// </summary>
    /// <remarks>
    /// On MSVC x64 an exception crossing a C ABI frame is undefined behaviour, and in
    /// practice terminates the process: the CLR dies with no stack trace, no finally
    /// blocks, and nothing flushed. So the callback shim catches everything, stashes
    /// it, tells native code to stop, and rethrows on the managed side of the boundary
    /// where there is a frame that can hold it.
    /// <para>
    /// The observable consequence is that the original exception survives, as the inner
    /// exception of a <see cref="PricingException"/>. This test is the only thing
    /// standing between a stray null dereference in somebody's progress bar and an
    /// unexplained process exit in production.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_exception_in_the_callback_is_carried_back_rather_than_unwinding_through_native_code()
    {
        using var engine = new PricingEngine();
        var thrown = new InvalidOperationException("the progress bar had a bad day");

        var ex = Assert.Throws<PricingException>(() =>
            engine.PriceMonteCarlo(Sane, 10_000_000, reportEvery: 1_000,
                progress: (_, _) => throw thrown));

        Assert.Equal(PricingStatus.Cancelled, ex.Status);
        Assert.Same(thrown, ex.InnerException);
    }

    [Fact]
    public void The_process_survives_a_callback_that_throws_repeatedly()
    {
        // If the shim leaked even one exception through a native frame, this loop
        // would not finish -- the test host would simply disappear. Passing is the
        // assertion.
        using var engine = new PricingEngine();
        for (var i = 0; i < 50; i++)
        {
            Assert.Throws<PricingException>(() =>
                engine.PriceMonteCarlo(Sane, 1_000_000, reportEvery: 100,
                    progress: (_, _) => throw new InvalidOperationException($"attempt {i}")));
        }
    }

    /// <summary>
    /// Proves the user-state channel survives a garbage collection mid-call, which is
    /// the empirical form of the reason the GCHandle is not pinned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The instinct when handing managed state to native code is to pin it. That
    /// instinct is wrong here, and expensively so: what crosses the boundary is
    /// <c>GCHandle.ToIntPtr</c>, which does not return the object's address. It returns
    /// an opaque token -- an index into the runtime's handle table. The collector may
    /// relocate the object underneath it and the token still resolves, because
    /// <c>GCHandle.FromIntPtr</c> asks the table rather than the heap.
    /// </para>
    /// <para>
    /// Pinning would not merely be unnecessary. <c>GCHandleType.Pinned</c> throws at
    /// run time for any object holding references, and the progress state holds a
    /// delegate and a CancellationToken -- so the pinned version compiles, passes every
    /// test that does not use a progress callback, and throws the first time somebody
    /// asks for one.
    /// </para>
    /// <para>
    /// This test forces the collection that would expose the difference: a full
    /// blocking compacting collection, from inside the callback, with fresh allocation
    /// either side of it.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_callback_state_survives_a_compacting_collection_during_the_native_call()
    {
        using var engine = new PricingEngine();
        var witness = new List<long>();
        var calls = 0;

        var price = engine.PriceMonteCarlo(Sane, 2_000_000, reportEvery: 100_000,
            progress: (done, total) =>
            {
                calls++;

                // Allocate, then force a compacting collection, then allocate again.
                // If the state object were reached through a raw address the runtime
                // was free to invalidate, this is where it would break.
                GC.KeepAlive(new byte[64 * 1024]);
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.KeepAlive(new byte[64 * 1024]);

                // Touching captured managed state after the collection: the closure,
                // the list, and the counter all have to still be there.
                Assert.True(total > 0);
                witness.Add(done);
                return true;
            });

        Assert.True(calls > 1, "needed at least two callbacks to span a collection");
        Assert.Equal(calls, witness.Count);
        Assert.Equal(witness.OrderBy(x => x), witness);
        Assert.True(double.IsFinite(price));
    }

    [Fact]
    public void A_run_without_a_callback_still_prices()
    {
        // With no callback and no cancellable token the shim passes a null function
        // pointer, so native code takes a different path entirely. It has to reach the
        // same answer.
        using var a = new PricingEngine(999);
        using var b = new PricingEngine(999);

        var withoutCallback = a.PriceMonteCarlo(Sane, 200_000);
        var withCallback = b.PriceMonteCarlo(Sane, 200_000, reportEvery: 50_000,
            progress: (_, _) => true);

        Assert.Equal(withoutCallback, withCallback);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void An_absurd_path_count_is_refused(long paths)
    {
        using var engine = new PricingEngine();
        var ex = Assert.Throws<PricingException>(() => engine.PriceMonteCarlo(Sane, paths));
        Assert.Equal(PricingStatus.BadArgument, ex.Status);
        Assert.Contains("paths", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_negative_reporting_interval_is_refused()
    {
        using var engine = new PricingEngine();
        Assert.Throws<PricingException>(
            () => engine.PriceMonteCarlo(Sane, 1000, reportEvery: -1, progress: (_, _) => true));
    }

    [Fact]
    public void A_rejected_run_leaves_no_handle_behind()
    {
        // The GCHandle for the progress state is allocated before the call and freed in
        // a finally. A path that returns early without reaching the finally would leak
        // one handle per rejected request -- invisible, unbounded, and only fatal after
        // several months of uptime.
        var (created0, destroyed0) = PricingEngine.NativeEngineStats();

        for (var i = 0; i < 100; i++)
        {
            using var engine = new PricingEngine();
            Assert.Throws<PricingException>(
                () => engine.PriceMonteCarlo(Sane, -1, reportEvery: 10, progress: (_, _) => true));
        }

        var (created1, destroyed1) = PricingEngine.NativeEngineStats();
        Assert.Equal(created1 - created0, destroyed1 - destroyed0);
    }
}
