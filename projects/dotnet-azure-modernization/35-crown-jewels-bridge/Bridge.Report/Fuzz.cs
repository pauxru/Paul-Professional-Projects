using System.Runtime.InteropServices;
using Bridge.Core;
using Bridge.Legacy;

namespace Bridge.Report;

/// <summary>What happened when one hostile input met one boundary.</summary>
public enum FuzzOutcome
{
    /// <summary>Accepted and returned a finite, non-negative price. Fine.</summary>
    Accepted,

    /// <summary>Refused with a status code. This is what a boundary is for.</summary>
    RejectedCleanly,

    /// <summary>
    /// Returned success, and a number that is not a price: NaN, infinite, or negative.
    /// The worst outcome in this table, and the most common one against the 2009
    /// boundary. A crash is loud. This gets booked.
    /// </summary>
    SilentlyWrong,

    /// <summary>Wrote outside the buffer it was given. Detected by a guard pattern.</summary>
    MemoryCorruption,

    /// <summary>Took the process with it.</summary>
    ProcessDied,
}

/// <summary>One generated hostile input.</summary>
/// <param name="Index">Position in the deterministic corpus. Reproducible from the seed.</param>
public readonly record struct FuzzCase(
    int Index, PricingOption Option, int Steps, int BatchCount, int BatchCapacity,
    int ErrorBufferCapacity, string Shape);

/// <summary>
/// Generates a deterministic corpus of inputs designed to be plausible enough to reach
/// the engine and hostile enough to break it.
/// </summary>
/// <remarks>
/// Purely random bit patterns are a poor fuzzer for a numerical ABI: almost every random
/// 64-bit pattern read as a double is an absurd magnitude that any check rejects
/// immediately, so the deep paths never run. This corpus therefore mixes three sources:
/// values drawn from a list of known-awkward doubles, values that are ordinary except in
/// one field, and genuinely random bits. The interesting failures come from the second
/// group, which is exactly the group a purely random fuzzer reaches least often.
/// </remarks>
public static class FuzzCorpus
{
    /// <summary>Doubles chosen because each one has broken something, somewhere.</summary>
    private static readonly double[] Awkward =
    [
        0.0, -0.0, 1.0, -1.0,
        double.NaN, double.PositiveInfinity, double.NegativeInfinity,
        double.Epsilon, -double.Epsilon,
        double.MaxValue, double.MinValue,
        1e-300, 1e300, -1e300,
        4.9406564584124654e-324,     // smallest subnormal
        2.2250738585072009e-308,     // largest subnormal
        1e16, 1e-16,
        -0.2,                         // the sign error that negates the opposite option
        0.2, 40.0, 42.0, 100.0, 1.0 / 3.0,
    ];

    private static readonly int[] AwkwardInts =
    [
        0, 1, -1, 2, -2, 7, 1000,
        int.MaxValue, int.MinValue,
        int.MaxValue - 1, 65536, -65536,
        20000, 20001,                 // either side of the hardened lattice limit
    ];

    /// <summary>A plausible option, used as the base for single-field mutations.</summary>
    private static readonly PricingOption Sane =
        new(42, 40, 0.10, 0.01, 0.20, 0.5, OptionKind.Call);

    public static IReadOnlyList<FuzzCase> Generate(ulong seed, int count)
    {
        var rng = new Xoshiro(seed);
        var cases = new List<FuzzCase>(count);

        for (var i = 0; i < count; i++)
        {
            var mode = i % 3;
            PricingOption o;
            string shape;

            switch (mode)
            {
                case 0:
                    // One field replaced with an awkward value; everything else sane.
                    // This is the shape that finds real defects, because it survives
                    // the shallow checks and reaches the arithmetic.
                    o = MutateOneField(Sane, rng, out var which);
                    shape = "one-field:" + which;
                    break;
                case 1:
                    // Every field awkward. Catches ordering assumptions in validation.
                    o = new PricingOption(
                        Pick(rng), Pick(rng), Pick(rng), Pick(rng), Pick(rng), Pick(rng),
                        (OptionKind)(int)(rng.NextUInt64() % 4));
                    shape = "all-awkward";
                    break;
                default:
                    // Raw bits. Mostly rejected, occasionally surprising.
                    o = new PricingOption(
                        rng.NextDoubleBits(), rng.NextDoubleBits(), rng.NextDoubleBits(),
                        rng.NextDoubleBits(), rng.NextDoubleBits(), rng.NextDoubleBits(),
                        (OptionKind)(int)(rng.NextUInt64() % 4));
                    shape = "random-bits";
                    break;
            }

            cases.Add(new FuzzCase(
                Index: i,
                Option: o,
                Steps: PickInt(rng),
                BatchCount: PickInt(rng),
                BatchCapacity: PickInt(rng),
                ErrorBufferCapacity: (int)(rng.NextUInt64() % 40),
                Shape: shape));
        }

        return cases;
    }

    private static PricingOption MutateOneField(PricingOption b, Xoshiro rng, out string which)
    {
        var field = (int)(rng.NextUInt64() % 7);
        var v = Pick(rng);
        which = field switch
        {
            0 => "spot", 1 => "strike", 2 => "rate", 3 => "dividend",
            4 => "volatility", 5 => "years", _ => "kind",
        };
        return field switch
        {
            0 => b with { Spot = v },
            1 => b with { Strike = v },
            2 => b with { Rate = v },
            3 => b with { Dividend = v },
            4 => b with { Volatility = v },
            5 => b with { Years = v },
            _ => b with { Kind = (int)(rng.NextUInt64() % 5) - 1 },
        };
    }

    private static double Pick(Xoshiro rng) => Awkward[(int)(rng.NextUInt64() % (ulong)Awkward.Length)];
    private static int PickInt(Xoshiro rng) => AwkwardInts[(int)(rng.NextUInt64() % (ulong)AwkwardInts.Length)];

    /// <summary>
    /// The same generator the C++ engine uses, so that a corpus index means the same
    /// thing on both sides of the boundary and a failing case can be replayed in either.
    /// </summary>
    public sealed class Xoshiro(ulong seed)
    {
        private readonly ulong[] _s = Seed(seed);

        private static ulong[] Seed(ulong seed)
        {
            var s = new ulong[4];
            for (var i = 0; i < 4; i++)
            {
                seed += 0x9E3779B97F4A7C15ul;
                var z = seed;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ul;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBul;
                s[i] = z ^ (z >> 31);
            }
            return s;
        }

        public ulong NextUInt64()
        {
            var result = System.Numerics.BitOperations.RotateLeft(_s[1] * 5, 7) * 9;
            var t = _s[1] << 17;
            _s[2] ^= _s[0];
            _s[3] ^= _s[1];
            _s[1] ^= _s[2];
            _s[0] ^= _s[3];
            _s[2] ^= t;
            _s[3] = System.Numerics.BitOperations.RotateLeft(_s[3], 45);
            return result;
        }

        public double NextDoubleBits() => BitConverter.UInt64BitsToDouble(NextUInt64());
    }
}

/// <summary>Runs a fuzz corpus against one DLL variant, in this process.</summary>
/// <remarks>
/// Running in-process is safe for the hardened build and emphatically not for the legacy
/// one, which is why <see cref="FuzzDriver"/> exists: it runs this class in a child
/// process so that a crash is data rather than the end of the experiment.
/// </remarks>
public sealed unsafe class FuzzRunner(NativeVariant variant) : IDisposable
{
    private const uint GuardPattern = 0xDEADBEEFu;
    private const int GuardBytes = 64;

    public FuzzOutcome RunOne(in FuzzCase c)
    {
        var engine = variant.CreateEngine();
        if (engine == nint.Zero)
        {
            return FuzzOutcome.ProcessDied;
        }
        try
        {
            var worst = FuzzOutcome.Accepted;
            worst = Worse(worst, European(engine, c));
            worst = Worse(worst, American(engine, c));
            worst = Worse(worst, Batch(engine, c));
            worst = Worse(worst, ErrorBuffer(engine, c));
            return worst;
        }
        finally
        {
            variant.Destroy(engine);
        }
    }

    private FuzzOutcome European(nint engine, in FuzzCase c)
    {
        var o = c.Option;
        double price;
        var status = variant.PriceEuropean(engine, &o, &price);
        return Judge(status, price);
    }

    private FuzzOutcome American(nint engine, in FuzzCase c)
    {
        // The lattice allocates from the step count, so a hostile step count is an
        // allocation-size attack. The hardened build bounds it; the legacy build hands
        // it to std::vector, where a negative int becomes an enormous size_t.
        //
        // Steps above the hardened limit are skipped for BOTH variants, so that the
        // comparison stays a comparison of safety rather than of patience: an
        // unbounded lattice at 2 billion steps does not crash, it simply never returns.
        if (c.Steps is < -8 or > 4096)
        {
            return FuzzOutcome.Accepted;
        }
        var o = c.Option;
        double price;
        var status = variant.PriceAmerican(engine, &o, c.Steps, &price);
        return Judge(status, price);
    }

    private FuzzOutcome Batch(nint engine, in FuzzCase c)
    {
        // Bounded to keep the experiment finite; the interesting case is not a huge
        // batch but a batch whose count exceeds the capacity it was given.
        var count = Math.Clamp(c.BatchCount, -4, 8);
        var capacity = Math.Clamp(c.BatchCapacity, -4, 8);
        if (count <= 0)
        {
            // Still worth calling: a negative count must not be treated as unsigned.
            var o0 = c.Option;
            double p0;
            var st0 = variant.PriceBatch(engine, &o0, count, &p0, 1);
            return st0 == PricingStatus.Ok ? FuzzOutcome.Accepted : FuzzOutcome.RejectedCleanly;
        }

        var opts = new PricingOption[count];
        Array.Fill(opts, c.Option);

        // The output buffer is unmanaged so a guard region can sit immediately after it
        // at a known address. A managed array would give the GC freedom to place other
        // live data there, and an overrun would corrupt something unrelated instead of
        // announcing itself.
        var bytes = (nuint)(count * sizeof(double)) + GuardBytes;
        var buf = (byte*)NativeMemory.Alloc(bytes);
        try
        {
            NativeMemory.Fill(buf, bytes, 0);
            var guard = (uint*)(buf + count * sizeof(double));
            for (var i = 0; i < GuardBytes / sizeof(uint); i++)
            {
                guard[i] = GuardPattern;
            }

            PricingStatus status;
            fixed (PricingOption* pOpts = opts)
            {
                status = variant.PriceBatch(engine, pOpts, count, (double*)buf, capacity);
            }

            for (var i = 0; i < GuardBytes / sizeof(uint); i++)
            {
                if (guard[i] != GuardPattern)
                {
                    return FuzzOutcome.MemoryCorruption;
                }
            }
            if (status != PricingStatus.Ok)
            {
                return FuzzOutcome.RejectedCleanly;
            }
            var prices = (double*)buf;
            var worst = FuzzOutcome.Accepted;
            for (var i = 0; i < count; i++)
            {
                worst = Worse(worst, Judge(PricingStatus.Ok, prices[i]));
            }
            return worst;
        }
        finally
        {
            NativeMemory.Free(buf);
        }
    }

    private FuzzOutcome ErrorBuffer(nint engine, in FuzzCase c)
    {
        // Provoke an error so there is a message to read, then ask for it with a buffer
        // that is almost certainly too small. The hardened build answers PJ_ERR_CAPACITY;
        // the legacy build strcpy's the message wherever it fits.
        var bad = c.Option with { Volatility = double.NaN };
        double ignored;
        _ = variant.PriceEuropean(engine, &bad, &ignored);

        var cap = Math.Clamp(c.ErrorBufferCapacity, 0, 40);
        var bytes = (nuint)cap + GuardBytes;
        var buf = (byte*)NativeMemory.Alloc(bytes);
        try
        {
            NativeMemory.Fill(buf, bytes, 0);
            var guard = (uint*)(buf + cap);
            for (var i = 0; i < GuardBytes / sizeof(uint); i++)
            {
                guard[i] = GuardPattern;
            }

            int needed;
            var status = variant.LastError(engine, buf, cap, &needed);

            for (var i = 0; i < GuardBytes / sizeof(uint); i++)
            {
                if (guard[i] != GuardPattern)
                {
                    return FuzzOutcome.MemoryCorruption;
                }
            }
            return status == PricingStatus.Ok ? FuzzOutcome.Accepted : FuzzOutcome.RejectedCleanly;
        }
        finally
        {
            NativeMemory.Free(buf);
        }
    }

    /// <summary>
    /// Classifies a (status, price) pair.
    /// </summary>
    /// <remarks>
    /// The judgement that matters: success plus a number that is not a price is worse
    /// than any failure. An option can never be worth less than nothing, so a negative
    /// result reported as success is a defect regardless of how plausible its magnitude
    /// looks -- and the negative-volatility case makes it look extremely plausible.
    /// </remarks>
    private static FuzzOutcome Judge(PricingStatus status, double price)
    {
        if (status != PricingStatus.Ok)
        {
            return FuzzOutcome.RejectedCleanly;
        }
        if (double.IsNaN(price) || double.IsInfinity(price) || price < 0.0)
        {
            return FuzzOutcome.SilentlyWrong;
        }
        return FuzzOutcome.Accepted;
    }

    private static FuzzOutcome Worse(FuzzOutcome a, FuzzOutcome b) => (FuzzOutcome)Math.Max((int)a, (int)b);

    public void Dispose() => variant.Dispose();
}
