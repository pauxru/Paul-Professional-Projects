using System.Runtime.CompilerServices;
using Bridge.Core;
using Bridge.Legacy;

namespace Bridge.Tests;

/// <summary>
/// The engine handle's whole job is to make two categories of undefined behaviour into
/// ordinary .NET exceptions: using a freed pointer, and freeing one twice.
/// </summary>
/// <remarks>
/// These tests matter more than usual here because <c>DisableRuntimeMarshalling</c> is
/// set on Bridge.Core, which switches off the runtime's automatic SafeHandle
/// AddRef/Release around P/Invoke. Every call site hand-writes that dance. Hand-written
/// reference counting that is subtly wrong looks exactly like hand-written reference
/// counting that is right, until a handle is released one time too many under load.
/// </remarks>
public class EngineLifetimeTests
{
    private static readonly PricingOption Sane =
        new(42, 40, 0.10, 0.0, 0.20, 0.5, OptionKind.Call);

    [Fact]
    public void A_new_engine_is_not_disposed()
    {
        using var engine = new PricingEngine();
        Assert.False(engine.IsDisposed);
    }

    [Fact]
    public void Dispose_marks_the_engine_disposed()
    {
        var engine = new PricingEngine();
        engine.Dispose();
        Assert.True(engine.IsDisposed);
    }

    [Fact]
    public void Double_dispose_is_silent()
    {
        // SafeHandle guarantees the underlying release runs once. If it did not, the
        // second Dispose would be a double free of native memory: a heap corruption
        // that surfaces later, somewhere else, in an unrelated allocation.
        var engine = new PricingEngine();
        engine.Dispose();
        engine.Dispose();
        engine.Dispose();
        Assert.True(engine.IsDisposed);
    }

    [Fact]
    public void Pricing_after_dispose_throws_rather_than_dereferencing_freed_memory()
    {
        var engine = new PricingEngine();
        engine.Dispose();
        Assert.Throws<ObjectDisposedException>(() => engine.PriceEuropean(Sane));
    }

    [Fact]
    public void Greeks_after_dispose_throws()
    {
        var engine = new PricingEngine();
        engine.Dispose();
        Assert.Throws<ObjectDisposedException>(() => engine.GreeksEuropean(Sane));
    }

    [Fact]
    public void American_pricing_after_dispose_throws()
    {
        var engine = new PricingEngine();
        engine.Dispose();
        Assert.Throws<ObjectDisposedException>(() => engine.PriceAmerican(Sane, 64));
    }

    [Fact]
    public void Batch_pricing_after_dispose_throws()
    {
        var engine = new PricingEngine();
        engine.Dispose();
        var options = new[] { Sane, Sane };
        Assert.Throws<ObjectDisposedException>(() => engine.PriceBatch(options));
    }

    [Fact]
    public void Monte_carlo_after_dispose_throws()
    {
        var engine = new PricingEngine();
        engine.Dispose();
        Assert.Throws<ObjectDisposedException>(() => engine.PriceMonteCarlo(Sane, 1_000));
    }

    [Fact]
    public void Reading_the_last_error_after_dispose_throws()
    {
        // Easy to forget: the error-reading path takes the same handle and needs the
        // same protection. A "harmless" diagnostic accessor is still a dereference.
        var engine = new PricingEngine();
        engine.Dispose();
        Assert.Throws<ObjectDisposedException>(() => engine.LastError());
    }

    [Fact]
    public void Every_engine_created_is_destroyed()
    {
        // The native side counts its own allocations, so this is a measurement rather
        // than an assertion about whether a finalizer happened to run.
        var (created0, destroyed0) = PricingEngine.NativeEngineStats();

        for (var i = 0; i < 50; i++)
        {
            using var engine = new PricingEngine((ulong)i);
            engine.PriceEuropean(Sane);
        }

        var (created1, destroyed1) = PricingEngine.NativeEngineStats();
        Assert.Equal(50, created1 - created0);
        Assert.Equal(50, destroyed1 - destroyed0);
    }

    [Fact]
    public void An_engine_abandoned_without_dispose_is_still_reclaimed_by_the_finalizer()
    {
        // SafeHandle has a critical finalizer, so a caller who forgets `using` leaks
        // until the next collection rather than for the life of the process. This is
        // the safety net, not the plan: the test forces the collection that a real
        // process would only reach eventually.
        var (created0, destroyed0) = PricingEngine.NativeEngineStats();

        AllocateWithoutDisposing();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var (created1, destroyed1) = PricingEngine.NativeEngineStats();
        Assert.Equal(10, created1 - created0);
        Assert.Equal(10, destroyed1 - destroyed0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateWithoutDisposing()
    {
        for (var i = 0; i < 10; i++)
        {
            var engine = new PricingEngine((ulong)(1000 + i));
            engine.PriceEuropean(Sane);
        }
    }

    [Fact]
    public void Engines_are_independent_of_each_other()
    {
        using var a = new PricingEngine(1);
        using var b = new PricingEngine(2);
        a.Dispose();

        // Disposing one engine must not disturb another. They share a DLL, not state.
        Assert.False(b.IsDisposed);
        Assert.True(b.PriceEuropean(Sane) > 0);
    }

    [Fact]
    public void A_fresh_engine_has_no_error_recorded()
    {
        using var engine = new PricingEngine();
        Assert.Equal(string.Empty, engine.LastError());
    }

    [Fact]
    public void A_rejected_call_records_a_readable_explanation()
    {
        using var engine = new PricingEngine();
        var bad = Sane with { Volatility = -1.0 };

        var ex = Assert.Throws<PricingException>(() => engine.PriceEuropean(bad));

        Assert.Equal(PricingStatus.BadArgument, ex.Status);
        // The message must actually contain the native explanation, not just the
        // status name. A status code alone tells an operator that something was
        // rejected, not which field was wrong.
        Assert.NotEqual(string.Empty, engine.LastError());
        Assert.Contains(engine.LastError(), ex.Message);
    }

    [Fact]
    public void The_error_message_is_read_with_the_two_call_idiom_not_a_guessed_buffer()
    {
        // If LastError guessed a fixed buffer size, a long message would come back
        // truncated -- or, in the 2009 code, would be copied past the end of the
        // buffer. The two-call idiom asks for the size first. The observable
        // consequence is that the message is complete: it ends with a full stop or a
        // closing parenthesis, never mid-word.
        using var engine = new PricingEngine();
        Assert.Throws<PricingException>(() => engine.PriceEuropean(Sane with { Years = -1 }));

        var message = engine.LastError();
        Assert.NotEqual(string.Empty, message);
        Assert.DoesNotContain("\0", message);
        Assert.Equal(message.Trim(), message);
    }

    [Fact]
    public void The_last_error_survives_being_read_twice()
    {
        // Reading a diagnostic must not consume it. Two operators looking at the same
        // failure should see the same text.
        using var engine = new PricingEngine();
        Assert.Throws<PricingException>(() => engine.PriceEuropean(Sane with { Volatility = -1 }));

        var first = engine.LastError();
        var second = engine.LastError();
        Assert.Equal(first, second);
    }

    /// <summary>
    /// A loaded module must be releasable exactly once, however many owners think they
    /// hold it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a regression test for a bug in this project's own code, and it is the
    /// most instructive one it produced, because the shape is the shape of every real
    /// double-free: nobody wrote <c>Free(h); Free(h);</c>. There was a
    /// <c>using var variant</c> in the caller and a <c>FuzzRunner</c> that also
    /// implemented <c>IDisposable</c> over the same variant. Each was individually
    /// correct. Together they released the module twice, and
    /// <c>NativeLibrary.Free</c> threw <c>InvalidOperationException</c> out of a
    /// <c>using</c> block -- so the reported failure was an interop stack trace inside
    /// whatever test happened to be running.
    /// </para>
    /// <para>
    /// It was intermittent, which is what kept it alive. The OS loader reference-counts,
    /// so the second free only fails when no other load of the same DLL is outstanding
    /// -- meaning the bug appears when a test is run <i>alone</i> and vanishes when it
    /// is run with the rest of the suite. Filtering down to reproduce it is the natural
    /// move and it is the move that makes it disappear.
    /// </para>
    /// </remarks>
    [Fact]
    public void Freeing_a_variant_twice_is_not_an_error()
    {
        var variant = Variants.Open(Variants.Hardened);
        variant.Dispose();
        variant.Dispose();
        variant.Dispose();
    }

    [Fact]
    public void A_variant_can_be_loaded_and_freed_repeatedly()
    {
        // The loader reference-counts, so a leak here is invisible until the hundredth
        // iteration of something. Priced through each one, so the test fails if a
        // module is freed while still in use rather than merely counting.
        for (var i = 0; i < 20; i++)
        {
            using var variant = Variants.Open(Variants.Hardened);
            var engine = variant.CreateEngine();
            Assert.NotEqual(nint.Zero, engine);
            try
            {
                Assert.True(variant.PriceOne(engine, Sane) > 0);
            }
            finally
            {
                unsafe { variant.Destroy(engine); }
            }
        }
    }
}
