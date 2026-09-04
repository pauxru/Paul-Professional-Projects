using System.Runtime.InteropServices;
using Bridge.Core;
using Bridge.Legacy;

namespace Bridge.Report;

/// <summary>
/// Performs one specific dangerous call against the 2009 boundary and reports what
/// happened, in a process that is expected not to survive some of them.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a crash cannot be asserted from inside the process that is
/// crashing. The interesting claims about the legacy boundary are of the form "this
/// input terminates the host", and the only way to turn that into a passing test is to
/// put a process boundary between the claim and the test runner.
/// </para>
/// <para>
/// Each probe is a single call, chosen because the hardened boundary refuses it for a
/// stated reason and the 2009 boundary does not check at all. None of them is
/// hypothetical: every one corresponds to an input a real caller produces by getting
/// one sign, one length or one null check wrong.
/// </para>
/// <para>
/// The exit codes matter as much as the crashes. Two of these five probes do not
/// crash, and those are the ones worth being frightened of.
/// </para>
/// </remarks>
public static class LegacyProbe
{
    private static readonly PricingOption Sane =
        new(42, 40, 0.10, 0.0, 0.20, 0.5, OptionKind.Put);

    /// <summary>The probe returned a finite number and the process lived.</summary>
    public const int Survived = 0;

    /// <summary>The probe was not recognised.</summary>
    public const int UnknownProbe = 2;

    /// <summary>The process lived, and returned something that is not a price.</summary>
    public const int SurvivedWithGarbage = 3;

    /// <summary>
    /// The process lived, and wrote outside the buffer it was given. Detected by a
    /// guard pattern rather than by a fault, because the fault is not reliable: a small
    /// overflow inside an allocation the allocator had already rounded up lands in
    /// padding and never touches an unmapped page.
    /// </summary>
    public const int SurvivedWithCorruption = 4;

    /// <summary>Guard value written either side of every buffer under test.</summary>
    /// <remarks>
    /// A signalling NaN would be tidier but the compiler is entitled to quieten it on
    /// a load. An ordinary, absurd magnitude cannot be produced by any option this
    /// engine will price, and compares bit for bit.
    /// </remarks>
    private const double Guard = -8.6421357911e300;

    public static unsafe int Run(string probe)
    {
        using var variant = Variants.Open(Variants.LegacyBoundary);
        var engine = variant.CreateEngine();
        if (engine == nint.Zero)
        {
            Console.Error.WriteLine("could not create a legacy engine");
            return UnknownProbe;
        }

        var option = Sane;
        double price;

        switch (probe)
        {
            case "american-negative-steps":
            {
                // The lattice sizes its working array from the step count and then
                // walks it. Nothing between the caller and that arithmetic checks the
                // sign, so a single wrong subtraction upstream -- steps = requested - 1
                // where requested was zero -- writes outside the allocation.
                price = 0;
                variant.PriceAmerican(engine, &option, -1, &price);
                break;
            }

            case "american-zero-steps":
            {
                // Survives. Returns a price that was never computed, for an option
                // that is genuinely worth something, and reports success while doing
                // it. This is the outcome the whole project is about.
                price = Guard;
                variant.PriceAmerican(engine, &option, 0, &price);
                break;
            }

            case "batch-short-buffer":
            {
                // pj_price_batch takes a capacity and the legacy build ignores it,
                // because in 2009 there was one caller and it always passed an array
                // of the right length. Sixty-four prices are written into room for one.
                //
                // The buffer is unmanaged and over-allocated so the overflow has
                // somewhere to land. A managed array would put the extra writes into
                // whatever the GC heap happened to hold next, which is both undetectable
                // and a genuinely worse thing to do to somebody's machine.
                const int count = 64;
                const int capacity = 1;
                const int slack = 256;

                var options = (PricingOption*)NativeMemory.Alloc(
                    (nuint)count, (nuint)sizeof(PricingOption));
                var buffer = (double*)NativeMemory.Alloc((nuint)(capacity + slack), sizeof(double));
                try
                {
                    for (var i = 0; i < count; i++) options[i] = Sane;
                    for (var i = 0; i < capacity + slack; i++) buffer[i] = Guard;

                    variant.PriceBatch(engine, options, count, buffer, capacity);
                    price = buffer[0];

                    // Every slot past the declared capacity should still hold the
                    // guard. Any that does not was written by a function that was told
                    // it had room for one.
                    var overwritten = 0;
                    for (var i = capacity; i < capacity + slack; i++)
                    {
                        if (!buffer[i].Equals(Guard)) overwritten++;
                    }

                    if (overwritten > 0)
                    {
                        variant.Destroy(engine);
                        Console.WriteLine(overwritten.ToString(
                            System.Globalization.CultureInfo.InvariantCulture));
                        Console.Out.Flush();
                        return SurvivedWithCorruption;
                    }
                }
                finally
                {
                    NativeMemory.Free(options);
                    NativeMemory.Free(buffer);
                }
                break;
            }

            case "error-probe-with-null-buffer":
            {
                // The two-call idiom every C API documents: ask for the required size
                // by passing no buffer, then allocate and ask again. The hardened
                // boundary supports it. The 2009 build reaches strcpy with a null
                // destination, so the caller doing the correct, documented thing is
                // the one who dies.
                int needed;
                variant.LastError(engine, null, 0, &needed);
                price = needed;
                break;
            }

            case "null-option":
            {
                // No null check anywhere in the legacy build.
                price = 0;
                variant.PriceEuropean(engine, null, &price);
                break;
            }

            default:
                Console.Error.WriteLine($"unknown probe '{probe}'");
                return UnknownProbe;
        }

        variant.Destroy(engine);

        Console.WriteLine(price.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Console.Out.Flush();

        // Surviving is itself a result, and so is what it survived with. A probe that
        // comes back holding a value nothing wrote, or one that is not a number, did
        // not crash and did not refuse. That deserves its own exit code rather than
        // being filed under success.
        if (price.Equals(Guard)) return SurvivedWithGarbage;
        return double.IsFinite(price) ? Survived : SurvivedWithGarbage;
    }
}
