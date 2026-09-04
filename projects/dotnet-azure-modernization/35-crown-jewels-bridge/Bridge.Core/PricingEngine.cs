using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Bridge.Core;

/// <summary>
/// Owns a native <c>pj_engine*</c>.
/// </summary>
/// <remarks>
/// <para>
/// A raw <c>nint</c> field would be enough to make this work and not enough to make it
/// safe. SafeHandle buys three things a field does not:
/// </para>
/// <list type="number">
/// <item>a critical finalizer, so the native allocation is released even if the caller
/// never disposes and even during an abrupt AppDomain teardown;</item>
/// <item>a reference count, so a call in flight on one thread cannot have the handle
/// freed out from under it by a Dispose on another;</item>
/// <item>a type that cannot be confused with any other <c>nint</c> in the codebase.</item>
/// </list>
/// <para>
/// Point 2 is the one that costs something, and the one this project measures. Note
/// that because Bridge.Core sets DisableRuntimeMarshalling, this handle can NOT be
/// passed directly to a LibraryImport signature -- SafeHandle marshalling is one of the
/// features that switch turns off. Every call site therefore has to perform the
/// AddRef/Release pairing by hand. See ADR-002.
/// </para>
/// </remarks>
public sealed class EngineHandle : SafeHandle
{
    private EngineHandle() : base(nint.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        // Runs in a constrained execution region from a critical finalizer. It must not
        // throw, must not allocate, and must not call anything that might. pj_engine_destroy
        // is a plain free that tolerates null, which is why the C header documents that
        // tolerance as part of the contract rather than as an accident.
        NativeMethods.pj_engine_destroy(handle);
        return true;
    }

    internal static unsafe EngineHandle Create(ulong seed)
    {
        nint raw;
        var status = NativeMethods.pj_engine_create(seed, &raw);
        if (status != PricingStatus.Ok)
        {
            throw new PricingException(status, "pj_engine_create failed");
        }

        var h = new EngineHandle();
        // SetHandle after construction, never in the constructor: if the constructor
        // threw between allocating the native object and storing it, the finalizer
        // would have nothing to release and the engine would leak.
        h.SetHandle(raw);
        return h;
    }
}

/// <summary>
/// The safe, idiomatic .NET face of the 2009 pricing engine.
/// </summary>
/// <remarks>
/// Everything in this class exists to make one of three guarantees: that a native
/// pointer is alive for the duration of a call, that a failure becomes an exception
/// rather than a plausible number, and that a managed exception never unwinds through
/// a native frame.
/// </remarks>
public sealed class PricingEngine : IDisposable
{
    private readonly EngineHandle _handle;

    public PricingEngine(ulong seed = 0x5EEDul)
    {
        AbiContract.Verify();
        _handle = EngineHandle.Create(seed);
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>True once the handle has been released.</summary>
    public bool IsDisposed => _handle.IsClosed;

    /// <summary>
    /// Process-wide count of engines created and destroyed, straight from the native
    /// side. The native code is willing to be counted so that a leak test can be a
    /// measurement instead of an assertion about a finalizer that may not have run.
    /// </summary>
    public static unsafe (long Created, long Destroyed) NativeEngineStats()
    {
        long created, destroyed;
        NativeMethods.pj_engine_stats(&created, &destroyed);
        return (created, destroyed);
    }

    /// <summary>
    /// Takes a reference count on the handle and returns the raw pointer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the manual replacement for the AddRef/Release that runtime marshalling
    /// would have done automatically for a SafeHandle parameter. Without it, this
    /// sequence is a use-after-free:
    /// </para>
    /// <code>
    /// Thread A: engine.PriceEuropean(...)   // reads the handle, enters native code
    /// Thread B: engine.Dispose()            // frees it
    /// Thread A:                             // native code dereferences freed memory
    /// </code>
    /// <para>
    /// <c>DangerousAddRef</c> also throws ObjectDisposedException if the handle is
    /// already closed, which is what turns "use after dispose" from undefined behaviour
    /// into an ordinary .NET exception.
    /// </para>
    /// <para>
    /// This was originally a <c>WithHandle&lt;T&gt;(Func&lt;nint, T&gt;)</c> helper,
    /// which is the tidier shape and does not compile: C# forbids taking the address of
    /// a local inside a lambda, and every one of these calls needs an out-pointer. The
    /// closure would also have allocated on every price. Losing the abstraction bought
    /// both correctness and a zero-allocation call path.
    /// </para>
    /// </remarks>
    private nint Acquire(ref bool added)
    {
        _handle.DangerousAddRef(ref added);
        return _handle.DangerousGetHandle();
    }

    private void Release(bool added)
    {
        if (added)
        {
            _handle.DangerousRelease();
        }
    }

    /// <summary>Reads the engine's last error message.</summary>
    /// <remarks>
    /// Uses the two-call idiom the C header documents: ask for the required size with a
    /// zero-capacity call, then ask again with a buffer that big. Guessing a fixed
    /// buffer size is how the 2009 boundary came to contain a strcpy.
    /// </remarks>
    public unsafe string LastError()
    {
        var added = false;
        try
        {
            var h = Acquire(ref added);
            int needed;
            var probe = NativeMethods.pj_last_error(h, null, 0, &needed);
            if (probe != PricingStatus.Capacity && probe != PricingStatus.Ok)
            {
                return $"<could not read error message: {probe}>";
            }
            if (needed <= 1)
            {
                return string.Empty;
            }

            var buffer = new byte[needed];
            fixed (byte* p = buffer)
            {
                var status = NativeMethods.pj_last_error(h, p, needed, &needed);
                if (status != PricingStatus.Ok)
                {
                    return $"<could not read error message: {status}>";
                }
            }
            // needed includes the NUL; the managed string must not.
            return Encoding.ASCII.GetString(buffer, 0, needed - 1);
        }
        finally
        {
            Release(added);
        }
    }

    private void Throw(PricingStatus status)
        => throw new PricingException(status, LastError());

    // -------------------------------------------------------------------- pricing

    public unsafe double PriceEuropean(in PricingOption option)
    {
        var local = option;
        var added = false;
        try
        {
            var h = Acquire(ref added);
            double price;
            var status = NativeMethods.pj_price_european(h, &local, &price);
            if (status != PricingStatus.Ok)
            {
                Throw(status);
            }
            return price;
        }
        finally
        {
            Release(added);
        }
    }

    public unsafe Greeks GreeksEuropean(in PricingOption option)
    {
        var local = option;
        var added = false;
        try
        {
            var h = Acquire(ref added);
            Greeks g;
            var status = NativeMethods.pj_greeks_european(h, &local, &g);
            if (status != PricingStatus.Ok)
            {
                Throw(status);
            }
            return g;
        }
        finally
        {
            Release(added);
        }
    }

    public unsafe double PriceAmerican(in PricingOption option, int steps)
    {
        var local = option;
        var added = false;
        try
        {
            var h = Acquire(ref added);
            double price;
            var status = NativeMethods.pj_price_american(h, &local, steps, &price);
            if (status != PricingStatus.Ok)
            {
                Throw(status);
            }
            return price;
        }
        finally
        {
            Release(added);
        }
    }

    /// <summary>
    /// Prices a whole portfolio in one boundary crossing, writing into a caller-supplied
    /// span.
    /// </summary>
    /// <remarks>
    /// The span overload exists so that a caller who already has a buffer does not have
    /// to allocate one. Both spans are pinned for the duration of the call, which is the
    /// entire reason this is safe: without the <c>fixed</c>, a compacting GC is free to
    /// move the arrays while native code holds pointers into them, and the corruption
    /// would appear later, elsewhere, in unrelated data.
    /// </remarks>
    public unsafe void PriceBatch(ReadOnlySpan<PricingOption> options, Span<double> prices)
    {
        if (prices.Length < options.Length)
        {
            throw new ArgumentException(
                $"prices has room for {prices.Length}, need {options.Length}", nameof(prices));
        }
        if (options.IsEmpty)
        {
            return;
        }

        var added = false;
        try
        {
            _handle.DangerousAddRef(ref added);
            fixed (PricingOption* pOpts = options)
            fixed (double* pOut = prices)
            {
                var status = NativeMethods.pj_price_batch(
                    _handle.DangerousGetHandle(), pOpts, options.Length, pOut, prices.Length);
                if (status != PricingStatus.Ok)
                {
                    Throw(status);
                }
            }
        }
        finally
        {
            if (added)
            {
                _handle.DangerousRelease();
            }
        }
    }

    public double[] PriceBatch(ReadOnlySpan<PricingOption> options)
    {
        var result = new double[options.Length];
        PriceBatch(options, result);
        return result;
    }

    // ---------------------------------------------------------------- monte carlo

    /// <summary>
    /// State handed to native code through the <c>void* user</c> channel.
    /// </summary>
    /// <remarks>
    /// This class deliberately uses a *normal* GCHandle, not a pinned one, and the
    /// distinction is the whole point of the channel.
    /// <para>
    /// The instinct is to pin: native code is holding a pointer to a managed object, so
    /// surely the object must not move. But what crosses the boundary is
    /// <see cref="GCHandle.ToIntPtr"/>, which does not return the object's address. It
    /// returns an opaque token -- an index into the runtime's handle table. The GC is
    /// free to relocate the object underneath it; the token still resolves.
    /// <see cref="GCHandle.FromIntPtr"/> on the far side asks the table, not the heap.
    /// </para>
    /// <para>
    /// Pinning would only matter if native code dereferenced the value, which would
    /// mean passing <c>AddrOfPinnedObject()</c> instead. And it is not merely
    /// unnecessary here, it is impossible: <c>GCHandle.Alloc(state, Pinned)</c> throws
    /// <c>ArgumentException("Object contains references")</c> because this type holds a
    /// delegate and a CancellationToken. The runtime refuses to pin anything whose
    /// fields the GC still has to trace.
    /// </para>
    /// <para>
    /// That failure arrives at run time, not compile time, and only on the first call
    /// that actually uses a progress callback -- which is why the version of this class
    /// that asked for Pinned survived every unit test that did not pass one.
    /// </para>
    /// </remarks>
    private sealed class ProgressState
    {
        public Func<long, long, bool>? Callback;
        public CancellationToken Token;
        public Exception? Escaped;
        public long Calls;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnProgress(long done, long total, void* user)
    {
        // This method runs on a native stack frame. If an exception leaves it, the
        // runtime has nowhere to put it and the process dies -- no stack trace, no
        // finally blocks, no flush. So it catches everything, parks the exception in
        // the state object, and returns 0 to ask the engine to stop. The managed caller
        // rethrows afterwards, from a frame that can actually carry it.
        try
        {
            var state = (ProgressState)GCHandle.FromIntPtr((nint)user).Target!;
            state.Calls++;

            if (state.Token.IsCancellationRequested)
            {
                return 0;
            }
            if (state.Callback is null)
            {
                return 1;
            }
            return state.Callback(done, total) ? 1 : 0;
        }
        catch (Exception ex)
        {
            try
            {
                var state = (ProgressState)GCHandle.FromIntPtr((nint)user).Target!;
                state.Escaped = ex;
            }
            catch
            {
                // If even recording the failure fails, cancelling is still the right
                // answer: continuing would run to completion on a callback that is
                // known to be broken.
            }
            return 0;
        }
    }

    /// <summary>
    /// Monte Carlo price with progress reporting and cancellation.
    /// </summary>
    /// <param name="reportEvery">
    /// Antithetic pairs between progress callbacks. Zero means never. This is a
    /// parameter rather than a constant because each callback is a transition back into
    /// managed code, which the report measures at roughly an order of magnitude more
    /// than a call in the other direction.
    /// </param>
    public unsafe double PriceMonteCarlo(
        in PricingOption option,
        long paths,
        long reportEvery = 0,
        Func<long, long, bool>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var local = option;
        var state = new ProgressState
        {
            Callback = progress,
            Token = cancellationToken,
        };

        // Normal, not Pinned. See the remarks on ProgressState: what crosses the
        // boundary is a handle-table token, not an address.
        var stateHandle = GCHandle.Alloc(state, GCHandleType.Normal);
        var added = false;
        try
        {
            _handle.DangerousAddRef(ref added);
            double price;
            var status = NativeMethods.pj_price_monte_carlo(
                _handle.DangerousGetHandle(),
                &local,
                paths,
                reportEvery,
                (progress is null && !cancellationToken.CanBeCanceled)
                    ? null
                    : &OnProgress,
                (void*)GCHandle.ToIntPtr(stateHandle),
                &price);

            // Order matters. A user exception is the most specific explanation for the
            // cancellation, so it wins; then an explicit token cancellation; only then
            // the generic "the callback said stop".
            if (state.Escaped is not null)
            {
                throw new PricingException(PricingStatus.Cancelled,
                    "the progress callback threw; the run was stopped at the boundary and " +
                    "the exception carried back to a frame that can hold it",
                    state.Escaped);
            }
            if (status == PricingStatus.Cancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(
                    "the progress callback asked the pricing run to stop");
            }
            if (status != PricingStatus.Ok)
            {
                Throw(status);
            }
            return price;
        }
        finally
        {
            if (added)
            {
                _handle.DangerousRelease();
            }
            LastProgressCallCount = state.Calls;
            stateHandle.Free();
        }
    }

    /// <summary>Number of times the native side called back into managed code on the
    /// most recent Monte Carlo run. Exposed so the report can price a reverse
    /// transition, not for callers to depend on.</summary>
    public long LastProgressCallCount { get; private set; }
}
