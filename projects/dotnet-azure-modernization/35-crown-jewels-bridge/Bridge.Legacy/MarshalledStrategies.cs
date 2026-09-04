using System.Runtime.InteropServices;
using Bridge.Core;

namespace Bridge.Legacy;

/// <summary>
/// The strategies that require the runtime marshaller, and therefore cannot exist in
/// Bridge.Core at all.
/// </summary>
/// <remarks>
/// Their presence in a separate assembly is not a code-organisation choice. It is forced
/// by DisableRuntimeMarshalling being assembly-wide. Any application that wants both
/// shapes carries this split whether it notices or not.
/// </remarks>
public static class MarshalledStrategies
{
    /// <summary>
    /// One P/Invoke per option, passing a class.
    /// </summary>
    /// <remarks>
    /// The runtime must allocate 56 bytes of unmanaged memory, copy eight fields into it,
    /// call, and free -- per option. None of that appears in the source. This is the
    /// single most common way a .NET interop layer becomes slow, because a class is
    /// simply what a C# programmer reaches for and nothing in the language pushes back.
    /// </remarks>
    public sealed class PerCallMarshalledClass : IMarshalStrategy
    {
        public string Name => "per-call, marshalled class";
        public string Shape => "per-call";
        public bool NeedsRuntimeMarshalling => true;
        public string Note => "N crossings plus N unmanaged allocations, copies and frees.";

        public void Price(nint engine, PricingOption[] options, double[] prices)
        {
            for (var i = 0; i < options.Length; i++)
            {
                var legacy = LegacyOption.From(options[i]);
                var status = MarshalledNativeMethods.pj_price_european(
                    engine, legacy, out var p);
                if (status != PricingStatus.Ok)
                {
                    throw new PricingException(status, "marshalled per-call pricing failed");
                }
                prices[i] = p;
            }
        }
    }

    /// <summary>
    /// One P/Invoke for the whole portfolio, letting the marshaller handle the arrays.
    /// </summary>
    /// <remarks>
    /// Because PricingOption is blittable, the marshaller is permitted to pin these
    /// arrays rather than copy them, and it does. The result is close to the hand-pinned
    /// strategy, which is worth knowing: "avoid the marshaller" is folklore, and the
    /// useful rule is narrower -- avoid marshalling NON-BLITTABLE things, and avoid
    /// crossing more often than you need to.
    /// </remarks>
    public sealed class BatchMarshalledArray : IMarshalStrategy
    {
        public string Name => "batch, marshalled array";
        public string Shape => "batch";
        public bool NeedsRuntimeMarshalling => true;
        public string Note => "1 crossing; the marshaller pins rather than copies because the element type is blittable.";

        public void Price(nint engine, PricingOption[] options, double[] prices)
        {
            if (options.Length == 0)
            {
                return;
            }
            var status = MarshalledNativeMethods.pj_price_batch(
                engine, options, options.Length, prices, prices.Length);
            if (status != PricingStatus.Ok)
            {
                throw new PricingException(status, "marshalled batch pricing failed");
            }
        }
    }

    /// <summary>
    /// One P/Invoke per option through a raw function pointer obtained at run time.
    /// </summary>
    /// <remarks>
    /// The floor. No generated stub, no marshalling, no SafeHandle reference count: an
    /// indirect call and nothing else. It is included to establish what the other
    /// strategies are being compared against, and it is not a recommendation -- it gives
    /// up every safety property the rest of this project exists to provide.
    /// </remarks>
    public sealed unsafe class PerCallRawFunctionPointer : IMarshalStrategy, IDisposable
    {
        private readonly NativeVariant _variant = Variants.Open(Variants.Hardened);

        public string Name => "per-call, raw function pointer";
        public string Shape => "per-call";
        public bool NeedsRuntimeMarshalling => false;
        public string Note => "N indirect calls, no stub and no reference count. The floor, and unsafe.";

        public void Price(nint engine, PricingOption[] options, double[] prices)
        {
            for (var i = 0; i < options.Length; i++)
            {
                var local = options[i];
                double p;
                var status = _variant.PriceEuropean(engine, &local, &p);
                if (status != PricingStatus.Ok)
                {
                    throw new PricingException(status, "raw pointer pricing failed");
                }
                prices[i] = p;
            }
        }

        public void Dispose() => _variant.Dispose();
    }

    public static IReadOnlyList<IMarshalStrategy> All { get; } =
    [
        new PerCallMarshalledClass(),
        new BatchMarshalledArray(),
    ];

    /// <summary>Every strategy from both assemblies, in a stable order.</summary>
    public static IReadOnlyList<IMarshalStrategy> Everything()
    {
        var all = new List<IMarshalStrategy>(BlittableStrategies.All);
        all.AddRange(All);
        all.Add(new PerCallRawFunctionPointer());
        return all;
    }
}
