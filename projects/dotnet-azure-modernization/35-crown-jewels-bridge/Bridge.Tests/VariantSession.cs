using Bridge.Core;
using Bridge.Legacy;
using System.Runtime.InteropServices;

namespace Bridge.Tests;

/// <summary>
/// A disposable pairing of a variant DLL and one engine created from it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NativeVariant"/> deliberately hands back a raw <c>nint</c> rather than a
/// SafeHandle, because the variants exist to be fuzzed and a wrapper that prevented
/// the observation would defeat the experiment. That is right for the report and
/// tedious for the tests, which want the same raw pointer but would rather not leak an
/// engine every time an assertion fails and unwinds past the destroy call.
/// </para>
/// <para>
/// So this is the thinnest possible <c>try/finally</c>: still a raw pointer, still no
/// protection at the call site, but reclaimed on the way out. The engine-count
/// assertions in <see cref="EngineLifetimeTests"/> are only meaningful if the tests
/// themselves are not leaking, so this type is load-bearing rather than convenient.
/// </para>
/// </remarks>
public sealed unsafe class VariantSession : IDisposable
{
    private readonly NativeVariant _variant;
    private nint _engine;

    public VariantSession(string variantName, ulong seed = 0x5EEDul)
    {
        _variant = Variants.Open(variantName);
        _engine = _variant.CreateEngine(seed);
        if (_engine == nint.Zero)
        {
            _variant.Dispose();
            throw new InvalidOperationException($"{variantName} refused to create an engine");
        }
    }

    public NativeVariant Variant => _variant;

    public nint Engine => _engine;

    public double PriceEuropean(in PricingOption option) => _variant.PriceOne(_engine, option);

    /// <summary>
    /// Prices without translating a failure into an exception, so a test can assert on
    /// the raw status and the raw output together -- including the case where the
    /// status is Ok and the output is not a number.
    /// </summary>
    public PricingStatus TryPriceEuropean(in PricingOption option, out double price)
    {
        var local = option;
        double result;
        var status = _variant.PriceEuropean(_engine, &local, &result);
        price = result;
        return status;
    }

    public PricingStatus TryPriceAmerican(in PricingOption option, int steps, out double price)
    {
        var local = option;
        double result;
        var status = _variant.PriceAmerican(_engine, &local, steps, &result);
        price = result;
        return status;
    }

    /// <summary>
    /// Calls the native batch entry point directly, with a buffer that is deliberately
    /// over-allocated and filled with a guard pattern beyond the declared capacity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns how many guard slots were overwritten, which is the only reliable way to
    /// ask this question. Waiting for an access violation does not work: a few hundred
    /// bytes of overrun lands inside allocator padding and the process carries on.
    /// </para>
    /// <para>
    /// The guard value is an ordinary absurd magnitude rather than a signalling NaN,
    /// because a signalling NaN can be quietened by the compiler simply on load, and a
    /// guard that changes when it is read is not a guard.
    /// </para>
    /// <para>
    /// This exists because a mutation test caught the gap it fills. The suite had a test
    /// for a short batch buffer, but it asserted on the managed wrapper's
    /// <c>ArgumentException</c> -- which is thrown before the P/Invoke, so deleting the
    /// native capacity check entirely left the suite green. The managed guard is the
    /// cheapest place to stop it; the native one is the only place that protects callers
    /// who are not using this wrapper, and it needs its own test.
    /// </para>
    /// </remarks>
    public const double Guard = -8.6421357911e300;

    public PricingStatus TryPriceBatchWithGuard(
        PricingOption[] options, int declaredCapacity, int guardSlots, out int overwritten)
    {
        var total = declaredCapacity + guardSlots;
        var buffer = (double*)NativeMemory.Alloc((nuint)total * sizeof(double));
        try
        {
            for (var i = 0; i < total; i++) buffer[i] = Guard;

            PricingStatus status;
            fixed (PricingOption* opts = options)
            {
                status = _variant.PriceBatch(
                    _engine, opts, options.Length, buffer, declaredCapacity);
            }

            overwritten = 0;
            for (var i = declaredCapacity; i < total; i++)
            {
                if (!buffer[i].Equals(Guard)) overwritten++;
            }
            return status;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    public void Dispose()
    {
        if (_engine != nint.Zero)
        {
            _variant.Destroy(_engine);
            _engine = nint.Zero;
        }
        _variant.Dispose();
    }
}
