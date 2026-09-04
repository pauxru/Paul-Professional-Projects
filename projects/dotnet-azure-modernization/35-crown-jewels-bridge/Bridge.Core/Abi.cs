using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// The single most consequential line in this assembly.
//
// DisableRuntimeMarshalling turns off the runtime's automatic marshalling for every
// P/Invoke declared here. That is what makes the fast path available: a struct crosses
// the boundary as bytes, with no per-call inspection of its fields. It also makes a
// whole category of convenience impossible -- no bool, no char, no string, no
// [MarshalAs], no non-blittable struct, anywhere in any signature in this assembly.
//
// It is an ASSEMBLY-WIDE switch. There is no per-call opt in. That is why Bridge.Legacy
// exists as a separate assembly: it is the only way to have both the fast path and the
// convenient one in the same process, and discovering that is what forces you to decide
// which parts of your interop surface deserve which.
[assembly: DisableRuntimeMarshalling]

namespace Bridge.Core;

/// <summary>Status codes returned by every fallible ABI entry point.</summary>
public enum PricingStatus
{
    Ok = 0,
    NullArgument = 1,
    BadArgument = 2,
    Capacity = 3,
    Overflow = 4,
    Cancelled = 5,
    NotConverged = 6,
    Internal = 7,
}

/// <summary>Call or put. An <c>int</c> because the C header refuses to expose an enum
/// whose width is implementation-defined.</summary>
public enum OptionKind
{
    Call = 0,
    Put = 1,
}

/// <summary>
/// The managed mirror of <c>pj_option</c>. Sequential layout, six doubles then two
/// 32-bit integers, 56 bytes with no padding.
/// <para>
/// <see cref="AbiContract"/> asserts the size and every field offset at start-up. That
/// check is not paranoia: if this struct and the C one disagree, nothing throws. The
/// fields silently read from the wrong offsets and the engine prices an option whose
/// strike is actually a dividend yield, returning a number that is finite, plausible
/// and completely wrong.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct PricingOption
{
    public double Spot { get; init; }
    public double Strike { get; init; }
    public double Rate { get; init; }
    public double Dividend { get; init; }
    public double Volatility { get; init; }
    public double Years { get; init; }
    public int Kind { get; init; }
    public int Reserved { get; init; }

    public PricingOption(double spot, double strike, double rate, double dividend,
                         double volatility, double years, OptionKind kind)
    {
        Spot = spot;
        Strike = strike;
        Rate = rate;
        Dividend = dividend;
        Volatility = volatility;
        Years = years;
        Kind = (int)kind;
        Reserved = 0;
    }
}

/// <summary>The managed mirror of <c>pj_greeks</c>: six doubles, 48 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Greeks(
    double Price, double Delta, double Gamma, double Vega, double Theta, double Rho);

/// <summary>
/// Thrown when the native side reports a failure. Carries the status code and, where
/// the engine recorded one, the native error message.
/// </summary>
public sealed class PricingException : Exception
{
    public PricingStatus Status { get; }

    public PricingException(PricingStatus status, string message)
        : base($"{status}: {message}")
        => Status = status;

    public PricingException(PricingStatus status, string message, Exception inner)
        : base($"{status}: {message}", inner)
        => Status = status;
}

/// <summary>
/// Locates the three native DLLs and asserts that the managed structs match the C ones.
/// </summary>
public static class AbiContract
{
    /// <summary>Major component of the ABI version this host was written against.</summary>
    public const int RequiredMajor = 1;

    private static readonly Lock Gate = new();
    private static string? _nativeDirectory;

    /// <summary>
    /// The directory holding pricing.dll, pricing_legacy.dll and pricing_fast.dll.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// If the directory cannot be found. It throws rather than returning a plausible
    /// guess: a resolver that silently falls back to the current directory will load
    /// whatever DLL happens to be sitting there, which is a supply-chain problem
    /// wearing a convenience costume.
    /// </exception>
    public static string NativeDirectory
    {
        get
        {
            lock (Gate)
            {
                return _nativeDirectory ??= FindNativeDirectory();
            }
        }
    }

    private static string FindNativeDirectory()
    {
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            var candidate = Path.Combine(probe.FullName, "native", "build", "bin");
            if (File.Exists(Path.Combine(candidate, "pricing.dll")))
            {
                return candidate;
            }
            probe = probe.Parent;
        }

        throw new InvalidOperationException(
            "could not locate native/build/bin/pricing.dll above " +
            AppContext.BaseDirectory +
            ". Run native/build first: cmake -S native -B native/build -G Ninja && cmake --build native/build");
    }

    /// <summary>
    /// Installs the resolver. Called from <see cref="NativeMethods"/>'s static
    /// constructor, which the runtime guarantees runs before the first P/Invoke through
    /// that class -- and every native call in this assembly goes through that class.
    /// </summary>
    /// <remarks>
    /// A <c>[ModuleInitializer]</c> would also work and is what this originally used,
    /// but CA2255 objects to module initializers in libraries and it is right to: they
    /// run at a point the consuming application cannot predict or opt out of. Hanging
    /// the installation off the type that needs it is both later and more precise.
    /// </remarks>
    internal static void Install()
    {
        NativeLibrary.SetDllImportResolver(typeof(AbiContract).Assembly, Resolve);
    }

    internal static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? path)
    {
        if (libraryName is not ("pricing" or "pricing_legacy" or "pricing_fast"))
        {
            return nint.Zero;
        }
        return NativeLibrary.Load(Path.Combine(NativeDirectory, libraryName + ".dll"));
    }

    /// <summary>
    /// Verifies, at run time, everything the compiler cannot: that the managed structs
    /// are the size and shape the C header promises, and that the loaded DLL speaks a
    /// version of the ABI this assembly understands.
    /// </summary>
    /// <remarks>
    /// The size checks are the load-bearing ones. C# will happily lay out a struct that
    /// is 56 bytes for entirely different reasons than the C compiler did, and every
    /// field would still be readable. Checking the total alone is not enough, so each
    /// offset is checked individually below.
    /// </remarks>
    public static void Verify()
    {
        Check(Unsafe.SizeOf<PricingOption>() == 56,
            $"PricingOption must be 56 bytes, is {Unsafe.SizeOf<PricingOption>()}");
        Check(Unsafe.SizeOf<Greeks>() == 48,
            $"Greeks must be 48 bytes, is {Unsafe.SizeOf<Greeks>()}");

        var probe = new PricingOption(1, 2, 3, 4, 5, 6, OptionKind.Put);
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<PricingOption>(in probe));
        Check(BitConverter.ToDouble(bytes[..8]) == 1.0, "Spot must be at offset 0");
        Check(BitConverter.ToDouble(bytes[8..16]) == 2.0, "Strike must be at offset 8");
        Check(BitConverter.ToDouble(bytes[16..24]) == 3.0, "Rate must be at offset 16");
        Check(BitConverter.ToDouble(bytes[24..32]) == 4.0, "Dividend must be at offset 24");
        Check(BitConverter.ToDouble(bytes[32..40]) == 5.0, "Volatility must be at offset 32");
        Check(BitConverter.ToDouble(bytes[40..48]) == 6.0, "Years must be at offset 40");
        Check(BitConverter.ToInt32(bytes[48..52]) == 1, "Kind must be at offset 48");
        Check(BitConverter.ToInt32(bytes[52..56]) == 0, "Reserved must be at offset 52");

        var version = NativeMethods.pj_abi_version();
        var major = version / 10000;
        Check(major == RequiredMajor,
            $"native ABI major version is {major}, this host requires {RequiredMajor}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("ABI contract violated: " + message);
        }
    }
}
