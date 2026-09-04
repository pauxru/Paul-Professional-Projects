using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Bridge.Core;

namespace Bridge.Legacy;

// Note what is NOT at the top of this file: [assembly: DisableRuntimeMarshalling].
//
// That is the entire reason this assembly exists. The switch is assembly-wide, so an
// application that wants the fast blittable path for its hot loop AND the convenient
// marshalled path for its once-a-day admin call cannot have both in one assembly. It
// must split them, which means the decision is architectural rather than local.
//
// Everything here is deliberately the "obvious" way to write interop -- the way it gets
// written when the goal is to make the call work rather than to make it cheap. The
// report measures what each of these habits costs.

/// <summary>
/// The same option, expressed as a class rather than a struct.
/// </summary>
/// <remarks>
/// This is what interop looks like when someone reaches for a class because classes are
/// what C# programmers reach for. It is never blittable -- a class is a reference type,
/// so the runtime must allocate an unmanaged block, copy every field into it, make the
/// call, and free it. That work happens on every single call and it is invisible in the
/// source.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public sealed class LegacyOption
{
    public double Spot;
    public double Strike;
    public double Rate;
    public double Dividend;
    public double Volatility;
    public double Years;
    public int Kind;
    public int Reserved;

    public static LegacyOption From(in PricingOption o) => new()
    {
        Spot = o.Spot, Strike = o.Strike, Rate = o.Rate, Dividend = o.Dividend,
        Volatility = o.Volatility, Years = o.Years, Kind = o.Kind, Reserved = o.Reserved,
    };
}

/// <summary>
/// P/Invoke declarations that rely on the runtime marshaller.
/// </summary>
internal static class MarshalledNativeMethods
{
    private const string Lib = "pricing";

    static MarshalledNativeMethods() => Variants.InstallResolver();

    /// <summary>Class parameter: an allocate, copy and free on every call.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PricingStatus pj_price_european(
        nint engine, [In] LegacyOption opt, out double outPrice);

    /// <summary>
    /// Array parameter. The element type is blittable, so the runtime is allowed to pin
    /// the array in place rather than copy it -- which is why this is much closer to the
    /// hand-pinned path than the class version, and why "the marshaller is slow" is too
    /// coarse a claim to be useful.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PricingStatus pj_price_batch(
        nint engine, [In] PricingOption[] opts, int count,
        [Out] double[] outPrices, int outCapacity);

    /// <summary>
    /// StringBuilder output: the classic convenient way to read a C string, and one of
    /// the most expensive things in the marshaller's repertoire. It allocates, calls,
    /// then transcodes the result from ANSI into UTF-16.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern PricingStatus pj_last_error(
        nint engine, StringBuilder? buf, int cap, out int needed);
}

/// <summary>
/// Loads the two non-production DLL variants and exposes them as raw function pointers.
/// </summary>
/// <remarks>
/// <para>
/// pricing_legacy.dll and pricing_fast.dll are experiment subjects, not dependencies.
/// Declaring them with LibraryImport would make them look like part of the product and
/// would bind their availability to assembly load. Loading them explicitly means a
/// missing variant is a clear failure at the point of use, and it demonstrates the
/// technique that matters when a native library name is only known at run time.
/// </para>
/// <para>
/// The resulting <c>delegate* unmanaged[Cdecl]</c> is the cheapest possible call shape:
/// no stub, no marshalling, an indirect call and nothing else.
/// </para>
/// </remarks>
public sealed unsafe class NativeVariant : IDisposable
{
    private readonly nint _module;

    public string Name { get; }

    public delegate* unmanaged[Cdecl]<ulong, nint*, PricingStatus> Create { get; }
    public delegate* unmanaged[Cdecl]<nint, void> Destroy { get; }
    public delegate* unmanaged[Cdecl]<nint, PricingOption*, double*, PricingStatus> PriceEuropean { get; }
    public delegate* unmanaged[Cdecl]<nint, PricingOption*, int, double*, PricingStatus> PriceAmerican { get; }
    public delegate* unmanaged[Cdecl]<nint, PricingOption*, int, double*, int, PricingStatus> PriceBatch { get; }
    public delegate* unmanaged[Cdecl]<nint, byte*, int, int*, PricingStatus> LastError { get; }
    public delegate* unmanaged[Cdecl]<nint, PricingOption*, long, long, nint, void*, double*, PricingStatus> PriceMonteCarlo { get; }

    internal NativeVariant(string name)
    {
        Name = name;
        _module = NativeLibrary.Load(Path.Combine(AbiContract.NativeDirectory, name + ".dll"));
        Create = (delegate* unmanaged[Cdecl]<ulong, nint*, PricingStatus>)
            NativeLibrary.GetExport(_module, "pj_engine_create");
        Destroy = (delegate* unmanaged[Cdecl]<nint, void>)
            NativeLibrary.GetExport(_module, "pj_engine_destroy");
        PriceEuropean = (delegate* unmanaged[Cdecl]<nint, PricingOption*, double*, PricingStatus>)
            NativeLibrary.GetExport(_module, "pj_price_european");
        PriceAmerican = (delegate* unmanaged[Cdecl]<nint, PricingOption*, int, double*, PricingStatus>)
            NativeLibrary.GetExport(_module, "pj_price_american");
        PriceBatch = (delegate* unmanaged[Cdecl]<nint, PricingOption*, int, double*, int, PricingStatus>)
            NativeLibrary.GetExport(_module, "pj_price_batch");
        LastError = (delegate* unmanaged[Cdecl]<nint, byte*, int, int*, PricingStatus>)
            NativeLibrary.GetExport(_module, "pj_last_error");
        PriceMonteCarlo = (delegate* unmanaged[Cdecl]<nint, PricingOption*, long, long, nint, void*, double*, PricingStatus>)
            NativeLibrary.GetExport(_module, "pj_price_monte_carlo");
    }

    /// <summary>Creates an engine and returns the raw pointer. The caller owns it.</summary>
    /// <remarks>
    /// Deliberately not wrapped in a SafeHandle. These variants are fuzz targets: the
    /// point is to observe what the unprotected boundary does, and a wrapper that
    /// prevented the observation would defeat the experiment. Nothing here is exported
    /// beyond the report and the tests.
    /// </remarks>
    public nint CreateEngine(ulong seed = 0x5EEDul)
    {
        nint raw;
        var status = Create(seed, &raw);
        return status == PricingStatus.Ok ? raw : nint.Zero;
    }

    public double PriceOne(nint engine, in PricingOption option)
    {
        var local = option;
        double price;
        var status = PriceEuropean(engine, &local, &price);
        if (status != PricingStatus.Ok)
        {
            throw new PricingException(status, $"{Name} refused the option");
        }
        return price;
    }

    public void Dispose()
    {
        if (_module != nint.Zero)
        {
            NativeLibrary.Free(_module);
        }
    }
}

/// <summary>Opens the three DLL variants by name.</summary>
public static class Variants
{
    public const string Hardened = "pricing";
    public const string LegacyBoundary = "pricing_legacy";
    public const string FastMath = "pricing_fast";

    private static int _installed;

    internal static void InstallResolver()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 0)
        {
            NativeLibrary.SetDllImportResolver(
                typeof(Variants).Assembly,
                (name, _, _) => name is Hardened or LegacyBoundary or FastMath
                    ? NativeLibrary.Load(Path.Combine(AbiContract.NativeDirectory, name + ".dll"))
                    : nint.Zero);
        }
    }

    public static NativeVariant Open(string name) => new(name);
}
