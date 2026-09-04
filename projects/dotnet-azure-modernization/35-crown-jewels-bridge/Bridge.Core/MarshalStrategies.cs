namespace Bridge.Core;

/// <summary>
/// One way of getting N options across the boundary and N prices back.
/// </summary>
/// <remarks>
/// Every implementation computes exactly the same prices from exactly the same inputs
/// using exactly the same native code. They differ only in how the bytes get there. That
/// is what makes the comparison in docs/results.md a measurement of the boundary rather
/// than of the engine, and it is why <c>PricesAreIdenticalAcrossEveryStrategy</c> in the
/// test suite is the most important test in the file: if two strategies disagreed on a
/// price, the benchmark would be timing two different programs.
/// </remarks>
public interface IMarshalStrategy
{
    string Name { get; }

    /// <summary>"per-call" crosses once per option; "batch" crosses once per call.</summary>
    string Shape { get; }

    /// <summary>
    /// True if this strategy needs the runtime marshaller, and therefore cannot live in
    /// an assembly that sets DisableRuntimeMarshalling.
    /// </summary>
    bool NeedsRuntimeMarshalling { get; }

    /// <summary>One-line explanation of what this costs and why.</summary>
    string Note { get; }

    void Price(nint engine, PricingOption[] options, double[] prices);
}

/// <summary>The strategies available in an assembly with runtime marshalling disabled.</summary>
public static unsafe class BlittableStrategies
{
    /// <summary>
    /// One P/Invoke per option, passing a pointer to a stack local.
    /// </summary>
    /// <remarks>
    /// This is the honest baseline for a chatty API. Nothing is copied and nothing is
    /// allocated; the only cost is the crossing itself, N times.
    /// </remarks>
    public sealed class PerCall : IMarshalStrategy
    {
        public string Name => "per-call, blittable pointer";
        public string Shape => "per-call";
        public bool NeedsRuntimeMarshalling => false;
        public string Note => "N crossings, zero copies. The cost of the boundary, undiluted.";

        public void Price(nint engine, PricingOption[] options, double[] prices)
        {
            for (var i = 0; i < options.Length; i++)
            {
                var local = options[i];
                double p;
                var status = NativeMethods.pj_price_european(engine, &local, &p);
                if (status != PricingStatus.Ok)
                {
                    throw new PricingException(status, "per-call pricing failed");
                }
                prices[i] = p;
            }
        }
    }

    /// <summary>
    /// The same, with the GC transition suppressed.
    /// </summary>
    /// <remarks>
    /// Removes the mode switch to preemptive GC around each call. Correct here only
    /// because pj_price_european is bounded at a few hundred nanoseconds. Applying this
    /// to a call that can block is how a process acquires unexplained multi-second GC
    /// pauses; the report demonstrates that rather than asserting it.
    /// </remarks>
    public sealed class PerCallNoGcTransition : IMarshalStrategy
    {
        public string Name => "per-call, SuppressGCTransition";
        public string Shape => "per-call";
        public bool NeedsRuntimeMarshalling => false;
        public string Note => "N crossings without the preemptive-mode switch. Safe only for bounded calls.";

        public void Price(nint engine, PricingOption[] options, double[] prices)
        {
            for (var i = 0; i < options.Length; i++)
            {
                var local = options[i];
                double p;
                var status = NativeMethods.pj_price_european_nogc(engine, &local, &p);
                if (status != PricingStatus.Ok)
                {
                    throw new PricingException(status, "per-call pricing failed");
                }
                prices[i] = p;
            }
        }
    }

    /// <summary>
    /// One P/Invoke for the whole portfolio, with both arrays pinned in place.
    /// </summary>
    /// <remarks>
    /// The managed arrays are handed to native code where they already live. No copy, no
    /// unmanaged allocation, one crossing. The <c>fixed</c> is not a formality: without
    /// it a compacting GC could relocate the arrays mid-call and the native writes would
    /// land in whatever occupies that memory next.
    /// </remarks>
    public sealed class BatchPinned : IMarshalStrategy
    {
        public string Name => "batch, pinned in place";
        public string Shape => "batch";
        public bool NeedsRuntimeMarshalling => false;
        public string Note => "1 crossing, zero copies. The managed heap IS the native buffer.";

        public void Price(nint engine, PricingOption[] options, double[] prices)
        {
            if (options.Length == 0)
            {
                return;
            }
            fixed (PricingOption* pOpts = options)
            fixed (double* pOut = prices)
            {
                var status = NativeMethods.pj_price_batch(
                    engine, pOpts, options.Length, pOut, prices.Length);
                if (status != PricingStatus.Ok)
                {
                    throw new PricingException(status, "batch pricing failed");
                }
            }
        }
    }

    /// <summary>
    /// One crossing, but into a buffer the caller allocated and copied into by hand.
    /// </summary>
    /// <remarks>
    /// This is what interop looks like when someone has heard that managed memory must
    /// not be handed to native code and has concluded that it must therefore be copied.
    /// It is entirely correct. It is also two allocations and 2N copies that the pinned
    /// version does not perform, and the report quantifies the difference.
    /// </remarks>
    public sealed class BatchCopyToUnmanaged : IMarshalStrategy
    {
        public string Name => "batch, hand-copied to unmanaged memory";
        public string Shape => "batch";
        public bool NeedsRuntimeMarshalling => false;
        public string Note => "1 crossing, 2 allocations and 2N copies. Correct, and needless.";

        public void Price(nint engine, PricingOption[] options, double[] prices)
        {
            if (options.Length == 0)
            {
                return;
            }
            var n = options.Length;
            var inBuf = System.Runtime.InteropServices.NativeMemory.Alloc(
                (nuint)n, (nuint)sizeof(PricingOption));
            var outBuf = System.Runtime.InteropServices.NativeMemory.Alloc(
                (nuint)n, sizeof(double));
            try
            {
                var src = (PricingOption*)inBuf;
                for (var i = 0; i < n; i++)
                {
                    src[i] = options[i];
                }
                var status = NativeMethods.pj_price_batch(
                    engine, src, n, (double*)outBuf, n);
                if (status != PricingStatus.Ok)
                {
                    throw new PricingException(status, "batch pricing failed");
                }
                var dst = (double*)outBuf;
                for (var i = 0; i < n; i++)
                {
                    prices[i] = dst[i];
                }
            }
            finally
            {
                System.Runtime.InteropServices.NativeMemory.Free(inBuf);
                System.Runtime.InteropServices.NativeMemory.Free(outBuf);
            }
        }
    }

    public static IReadOnlyList<IMarshalStrategy> All { get; } =
    [
        new PerCall(),
        new PerCallNoGcTransition(),
        new BatchPinned(),
        new BatchCopyToUnmanaged(),
    ];
}
