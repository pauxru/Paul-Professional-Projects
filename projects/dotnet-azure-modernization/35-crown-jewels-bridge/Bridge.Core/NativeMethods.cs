using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bridge.Core;

/// <summary>
/// The raw P/Invoke surface for pricing.dll. Nothing outside Bridge.Core should call
/// these directly: every one of them will happily accept a dangling pointer.
/// </summary>
/// <remarks>
/// <para>
/// <c>LibraryImport</c> rather than <c>DllImport</c>: the source generator emits the
/// stub at compile time, so the marshalling is visible in the build output and
/// AOT-compatible, and -- because this assembly has DisableRuntimeMarshalling -- any
/// non-blittable type in a signature is a compile error rather than a silent
/// per-call reflection cost.
/// </para>
/// <para>
/// Every declaration names its calling convention explicitly. On x64 Windows there is
/// only one, so this is documentation today and a bug prevented on the day someone
/// builds for x86 or ARM32.
/// </para>
/// </remarks>
internal static unsafe partial class NativeMethods
{
    private const string Lib = "pricing";

    // Runs before the first call to any member of this class, which is before the first
    // P/Invoke this assembly can possibly make. That ordering is the whole point: the
    // resolver has to be in place before the runtime tries to locate "pricing".
    static NativeMethods() => AbiContract.Install();

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int pj_abi_version();

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial PricingStatus pj_engine_create(ulong seed, nint* @out);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void pj_engine_destroy(nint engine);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial PricingStatus pj_last_error(nint engine, byte* buf, int cap, int* needed);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial PricingStatus pj_price_european(nint engine, PricingOption* opt, double* outPrice);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial PricingStatus pj_greeks_european(nint engine, PricingOption* opt, Greeks* @out);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial PricingStatus pj_price_american(nint engine, PricingOption* opt, int steps, double* outPrice);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial PricingStatus pj_price_batch(nint engine, PricingOption* opts, int count, double* outPrices, int outCapacity);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial PricingStatus pj_price_monte_carlo(
        nint engine, PricingOption* opt, long paths, long reportEvery,
        delegate* unmanaged[Cdecl]<long, long, void*, int> progress, void* user, double* outPrice);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void pj_engine_stats(long* created, long* destroyed);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void pj_noop();

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial double pj_noop_option(PricingOption* opt);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void pj_burn(int micros);

    // ---------------------------------------------------------------------------
    // The same three functions again, with SuppressGCTransition.
    //
    // This attribute removes the transition to preemptive GC mode around the call. The
    // saving is real and large in relative terms -- see docs/results.md -- and the price
    // is that the runtime cannot suspend this thread for the whole duration of the call.
    // A collection that needs every thread at a safe point will wait for the slowest
    // native call to return.
    //
    // It is therefore correct ONLY for calls that are short and bounded. pj_burn is
    // declared both ways precisely so the report can measure what happens when that
    // rule is broken, rather than merely warning about it.
    // ---------------------------------------------------------------------------

    [LibraryImport(Lib, EntryPoint = "pj_noop")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    [SuppressGCTransition]
    internal static partial void pj_noop_nogc();

    [LibraryImport(Lib, EntryPoint = "pj_price_european")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    [SuppressGCTransition]
    internal static partial PricingStatus pj_price_european_nogc(nint engine, PricingOption* opt, double* outPrice);

    [LibraryImport(Lib, EntryPoint = "pj_burn")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    [SuppressGCTransition]
    internal static partial void pj_burn_nogc(int micros);
}
