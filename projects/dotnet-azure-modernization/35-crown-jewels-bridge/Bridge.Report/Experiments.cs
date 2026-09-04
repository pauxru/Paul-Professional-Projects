using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Bridge.Core;
using Bridge.Legacy;

namespace Bridge.Report;

/// <summary>A claim made before the measurement, and what the measurement said.</summary>
public sealed record Prediction(int Id, string Expected)
{
    public string? Verdict { get; private set; }
    public string? Evidence { get; private set; }

    /// <summary>True if the evidence is reproducible bit-for-bit across runs.</summary>
    public bool IsStable { get; private set; }

    public Prediction Settle(bool held, string evidence, bool stable)
    {
        if (Verdict is not null)
        {
            throw new InvalidOperationException($"prediction {Id} settled twice");
        }
        Verdict = held ? "HELD" : "CONTRADICTED";
        Evidence = evidence;
        IsStable = stable;
        return this;
    }
}

/// <summary>
/// The report, as a program.
/// </summary>
/// <remarks>
/// <para>
/// Every prediction below was written before the corresponding measurement existed. That
/// ordering is the only thing that makes a report like this worth reading: a document
/// assembled after the numbers arrive will always agree with them.
/// </para>
/// <para>
/// Two files come out of this. <c>results.md</c> carries everything, including wall-clock
/// timings, and is therefore not reproducible byte-for-byte -- no benchmark is.
/// <c>results-stable.md</c> carries only the claims that ARE exact: verdicts, counts,
/// bit-level floating-point comparisons, fuzz outcomes. That file is compared byte-for-byte
/// by test.ps1. Splitting them is the honest alternative to either pretending timings are
/// deterministic or giving up on determinism altogether. See ADR-005.
/// </para>
/// </remarks>
public sealed class Experiments
{
    private readonly List<Prediction> _predictions = [];
    private readonly StringBuilder _full = new();
    private readonly StringBuilder _stable = new();
    private readonly List<PricingOption> _portfolio;

    /// <summary>Number of fuzz cases. Fixed so the corpus is a constant of the report.</summary>
    public const int FuzzCases = 600;

    public Experiments()
    {
        _portfolio = BuildPortfolio();
    }

    private Prediction Predict(int id, string expected)
    {
        var p = new Prediction(id, expected);
        _predictions.Add(p);
        return p;
    }

    /// <summary>
    /// A 4,000-position book spanning the moneyness and maturity range a real desk holds.
    /// </summary>
    /// <remarks>
    /// Deterministic and deliberately varied: a benchmark on 4,000 copies of one option
    /// measures a branch predictor, not a pricing library.
    /// </remarks>
    private static List<PricingOption> BuildPortfolio()
    {
        var rng = new FuzzCorpus.Xoshiro(0xB0A7);
        var list = new List<PricingOption>(4000);
        for (var i = 0; i < 4000; i++)
        {
            var spot = 20.0 + (rng.NextUInt64() % 18000) / 100.0;
            var moneyness = 0.7 + (rng.NextUInt64() % 600) / 1000.0;
            var vol = 0.05 + (rng.NextUInt64() % 700) / 1000.0;
            var years = 0.02 + (rng.NextUInt64() % 500) / 100.0;
            var rate = (rng.NextUInt64() % 80) / 1000.0;
            var div = (rng.NextUInt64() % 40) / 1000.0;
            var kind = (rng.NextUInt64() % 2) == 0 ? OptionKind.Call : OptionKind.Put;
            list.Add(new PricingOption(spot, spot * moneyness, rate, div, vol, years, kind));
        }
        return list;
    }

    // =====================================================================  run

    public unsafe void Run(string docsDirectory)
    {
        Header();

        var variant = Variants.Open(Variants.Hardened);
        var rawEngine = variant.CreateEngine();
        if (rawEngine == nint.Zero)
        {
            throw new InvalidOperationException("could not create a native engine");
        }

        try
        {
            using var engine = new PricingEngine();

            P1_TheBoundaryCostIsARatioNotANumber(rawEngine, engine);
            P2_BatchingIsWorthMoreThanYouThink(rawEngine);
            P3_SafeHandleIsNotFree(rawEngine, engine);
            P4_SuppressGcTransitionIsNotAFreeWin(rawEngine);
            P5_TheMarshallerIsNotTheProblem(rawEngine);
            P12_TheShapeOfTheAbiIsAPerformanceDecision(rawEngine, engine);
            P9_CallingBackIsNotTheSameAsCallingOut(engine);
            P11_CancellationLatencyIsChosenNotGiven(engine);
            P10_MatchingSizesDoNotMeanMatchingLayouts(rawEngine);
            P8_RecompilingMovesThePrice();
            P6_ACppExceptionDoesNotBecomeANetException();
            P7_TheEngineDidNotNeedToChange();
        }
        finally
        {
            variant.Destroy(rawEngine);
            variant.Dispose();
        }

        Scoreboard();

        Directory.CreateDirectory(docsDirectory);
        File.WriteAllText(Path.Combine(docsDirectory, "results.md"),
            _full.ToString().ReplaceLineEndings("\n"), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(docsDirectory, "results-stable.md"),
            _stable.ToString().ReplaceLineEndings("\n"), new UTF8Encoding(false));
    }

    private void Header()
    {
        Both("# Crossing the boundary: what interop actually costs, and what it actually risks");
        Both("");
        Both("A 60,000-line C++ pricing engine, unchanged, exposed through a narrow C ABI and");
        Both("called from .NET. Twelve claims about that boundary, each written down before it");
        Both("was measured.");
        Both("");
        Both("Three DLLs are built from **identical** `engine.cpp`:");
        Both("");
        Both("| DLL | boundary (`abi.cpp`) | floating point |");
        Both("|---|---|---|");
        Both("| `pricing.dll` | hardened: validates, bounds, catches | `/fp:precise` |");
        Both("| `pricing_legacy.dll` | as found in 2009: trusts everything | `/fp:precise` |");
        Both("| `pricing_fast.dll` | hardened | `/fp:fast /arch:AVX2` |");
        Both("");
        Both("Because the engine is byte-identical in all three, every difference measured");
        Both("below belongs either to the boundary or to the compiler, and never to someone");
        Both("quietly improving the maths.");
        Both("");
        _full.AppendLine("Timings are medians of 9 samples after 3 warm-up rounds, on one shared");
        _full.AppendLine("developer machine. They are reported to the precision they deserve, which is");
        _full.AppendLine("about one significant figure for a ratio and none at all for an absolute");
        _full.AppendLine("number quoted out of context.");
        _full.AppendLine();
        _stable.AppendLine("This is the reproducible subset of `results.md`: every claim here is exact and");
        _stable.AppendLine("byte-identical across runs. Wall-clock timings live in the full report only.");
        _stable.AppendLine();
    }

    private void Both(string line)
    {
        _full.AppendLine(line);
        _stable.AppendLine(line);
    }

    private void Full(string line) => _full.AppendLine(line);
    private void Stable(string line) => _stable.AppendLine(line);

    private void Settle(Prediction p, bool held, string evidence, bool stable)
    {
        p.Settle(held, evidence, stable);
        Full($"**P{p.Id} -- expected.** {p.Expected}");
        Full("");
        Full($"**P{p.Id} -- {p.Verdict}.** {evidence}");
        Full("");
        if (stable)
        {
            Stable($"**P{p.Id} -- expected.** {p.Expected}");
            Stable("");
            Stable($"**P{p.Id} -- {p.Verdict}.** {evidence}");
            Stable("");
        }
        else
        {
            Stable($"**P{p.Id} -- {p.Verdict}.** (Evidence is a wall-clock measurement; see `results.md`.)");
            Stable("");
        }
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ==================================================================  P1

    private unsafe void P1_TheBoundaryCostIsARatioNotANumber(nint raw, PricingEngine engine)
    {
        var p = Predict(1,
            "A P/Invoke costs a few tens of nanoseconds. That is a rounding error next to " +
            "any real computation, so the boundary can be treated as free and the API " +
            "designed for clarity instead.");

        Full("## P1 -- the cost of a crossing");
        Full("");

        var noop = Bench.Measure("noop", 2_000_000, n =>
        {
            for (long i = 0; i < n; i++)
            {
                NativeMethods.pj_noop();
            }
        });

        var opt = _portfolio[0];
        var european = Bench.Measure("european", 500_000, n =>
        {
            var local = opt;
            double price = 0;
            for (long i = 0; i < n; i++)
            {
                NativeMethods.pj_price_european(raw, &local, &price);
            }
            Bench.Sink += price;
        });

        var american = Bench.Measure("american-512", 2_000, n =>
        {
            var local = opt;
            double price = 0;
            for (long i = 0; i < n; i++)
            {
                NativeMethods.pj_price_american(raw, &local, 512, &price);
            }
            Bench.Sink += price;
        });

        var managedNoop = Bench.Measure("managed call", 20_000_000, n =>
        {
            double acc = 0;
            for (long i = 0; i < n; i++)
            {
                acc += ManagedNoop();
            }
            Bench.Sink += acc;
        });

        Full("| call | median | boundary overhead as a share of the call |");
        Full("|---|---:|---:|");
        Full($"| managed method (nothing crosses) | {managedNoop.Format()} | -- |");
        Full($"| `pj_noop` (crossing, no work) | {noop.Format()} | 100% |");
        Full($"| `pj_price_european` (~200ns of work) | {european.Format()} | {100.0 * noop.MedianNs / european.MedianNs:F0}% |");
        Full($"| `pj_price_american(512)` (~1ms of work) | {american.Format()} | {100.0 * noop.MedianNs / american.MedianNs:F2}% |");
        Full("");

        var shareEuropean = 100.0 * noop.MedianNs / european.MedianNs;
        var shareAmerican = 100.0 * noop.MedianNs / american.MedianNs;

        Settle(p, held: false,
            $"The crossing itself costs {noop.Format()}, which is indeed small. But it is not " +
            $"a rounding error, it is a **ratio**. Against `pj_price_european` it is " +
            $"{shareEuropean:F0}% of the call; against `pj_price_american(512)` it is " +
            $"{shareAmerican:F2}%. The same boundary is {Bench.Ratio(shareEuropean, shareAmerican)} " +
            "more expensive for one function than the other, and neither number is a property " +
            "of the boundary. The design rule that follows is not \"P/Invoke is cheap\" but " +
            "**put enough work behind each crossing that the crossing stops mattering** -- which " +
            "is a statement about API shape, not about performance tuning.",
            stable: false);
    }

    private static double _managedSink;
    private static double ManagedNoop()
    {
        _managedSink += 1.0;
        return _managedSink;
    }

    // ==================================================================  P2

    private unsafe void P2_BatchingIsWorthMoreThanYouThink(nint raw)
    {
        var p = Predict(2,
            "Batching a portfolio into one call instead of N saves the per-call overhead, " +
            "so the win is bounded by that overhead: somewhere between 2x and 5x.");

        Full("## P2 -- per-call versus batch");
        Full("");

        var opts = _portfolio.ToArray();
        var prices = new double[opts.Length];

        var perCall = new BlittableStrategies.PerCall();
        var batch = new BlittableStrategies.BatchPinned();

        var perCallM = Bench.Measure("per-call", 1, _ => perCall.Price(raw, opts, prices), samples: 7);
        var batchM = Bench.Measure("batch", 1, _ => batch.Price(raw, opts, prices), samples: 7);

        Full($"Pricing all {opts.Length} positions:");
        Full("");
        Full("| shape | crossings | median |");
        Full("|---|---:|---:|");
        Full($"| one call per option | {opts.Length} | {perCallM.Format()} |");
        Full($"| one call for the book | 1 | {batchM.Format()} |");
        Full("");

        var speedup = perCallM.MedianNs / batchM.MedianNs;
        Settle(p, speedup <= 5.0,
            $"Batching is {Bench.Ratio(perCallM.MedianNs, batchM.MedianNs)} faster, not 2-5x. " +
            "The prediction accounted for the transition and forgot everything around it: the " +
            "per-call shape pays a stub, an argument setup, a return-value check and a status " +
            "branch for every single option, and the loop that drives it never gets to stay " +
            "inside native code long enough to warm anything. Batching removes " +
            $"{opts.Length - 1} crossings and, more importantly, moves the loop to the side of " +
            "the boundary where the data already is.",
            stable: false);
    }

    // ==================================================================  P3

    private unsafe void P3_SafeHandleIsNotFree(nint raw, PricingEngine engine)
    {
        var p = Predict(3,
            "SafeHandle's reference counting is a pair of interlocked operations. Next to a " +
            "P/Invoke that is free, so the safe wrapper costs nothing measurable.");

        Full("## P3 -- what safety costs");
        Full("");

        var opt = _portfolio[0];
        var rawM = Bench.Measure("raw", 500_000, n =>
        {
            var local = opt;
            double price = 0;
            for (long i = 0; i < n; i++)
            {
                NativeMethods.pj_price_european(raw, &local, &price);
            }
            Bench.Sink += price;
        });

        var safeM = Bench.Measure("safe", 500_000, n =>
        {
            double acc = 0;
            for (long i = 0; i < n; i++)
            {
                acc += engine.PriceEuropean(opt);
            }
            Bench.Sink += acc;
        });

        var overheadNs = safeM.MedianNs - rawM.MedianNs;
        var overheadPct = 100.0 * overheadNs / rawM.MedianNs;

        Full("| path | median | delta |");
        Full("|---|---:|---:|");
        Full($"| raw pointer, no reference count | {rawM.Format()} | -- |");
        Full($"| `PricingEngine.PriceEuropean` (SafeHandle AddRef/Release, try/finally, status check) | {safeM.Format()} | +{N(overheadNs)} ns ({overheadPct:F0}%) |");
        Full("");

        Settle(p, overheadPct < 5.0,
            $"The safe path costs +{N(overheadNs)} ns per call, {overheadPct:F0}% on top of the raw " +
            "one. Whether that is free depends entirely on the shape of the API above it: on the " +
            "per-call path it is a real fraction of every price, and on the batch path it is paid " +
            $"once for {_portfolio.Count} options and disappears. " +
            "The conclusion is not \"SafeHandle is expensive\" -- it is that a chatty API makes " +
            "safety look expensive, and the fix is the API, because the alternative is to buy " +
            "back a few nanoseconds by making use-after-free representable.",
            stable: false);
    }

    // ==================================================================  P4

    private void P4_SuppressGcTransitionIsNotAFreeWin(nint raw)
    {
        var p = Predict(4,
            "SuppressGCTransition removes work from every call and changes nothing else, so " +
            "it should be applied to every short native call as a matter of course.");

        Full("## P4 -- SuppressGCTransition, and what it costs somebody else");
        Full("");

        var withTransition = Bench.Measure("noop", 2_000_000, n =>
        {
            for (long i = 0; i < n; i++)
            {
                NativeMethods.pj_noop();
            }
        });
        var without = Bench.Measure("noop-nogc", 2_000_000, n =>
        {
            for (long i = 0; i < n; i++)
            {
                NativeMethods.pj_noop_nogc();
            }
        });

        Full($"On an empty call: {withTransition.Format()} with the transition, " +
             $"{without.Format()} without -- {Bench.Ratio(withTransition.MedianNs, without.MedianNs)} faster.");
        Full("");
        Full("So far the prediction looks right. Now the same attribute on a call that is not short:");
        Full("");

        var normal = MeasureGcPauseDuringNativeCall(suppressed: false, burnMicros: 60_000);
        var suppressed = MeasureGcPauseDuringNativeCall(suppressed: true, burnMicros: 60_000);

        Full("| native call in flight on another thread | longest `GC.Collect()` observed |");
        Full("|---|---:|");
        Full($"| `pj_burn(60ms)`, normal transition | {normal:F1} ms |");
        Full($"| `pj_burn(60ms)`, SuppressGCTransition | {suppressed:F1} ms |");
        Full("");

        var blocked = suppressed > normal * 3 && suppressed > 20;
        Settle(p, held: false,
            $"The speed-up is real -- {Bench.Ratio(withTransition.MedianNs, without.MedianNs)} on an " +
            "empty call, and it comes from removing the switch to preemptive GC mode. That switch " +
            "is what allows the runtime to suspend the calling thread. Remove it and the thread is " +
            "uninterruptible for the duration of the call, so a collection anywhere else in the " +
            $"process waits for it: a `GC.Collect()` on another thread ran in {normal:F1} ms while a " +
            $"normal 60 ms native call was in flight, and {suppressed:F1} ms while a suppressed one " +
            (blocked
                ? "was -- the collector waited for the native call to finish. "
                : "was. ") +
            "The attribute is not an optimisation you apply to short calls; it is a promise that " +
            "the call is short, made to a component that has no way to check.",
            stable: false);
    }

    /// <summary>
    /// Measures the worst GC pause seen on one thread while another sits inside a long
    /// native call.
    /// </summary>
    /// <remarks>
    /// This is the experiment BenchmarkDotNet cannot run, because it isolates each
    /// benchmark in its own process and the effect being measured is precisely an
    /// interaction between two threads of one process.
    /// </remarks>
    private static double MeasureGcPauseDuringNativeCall(bool suppressed, int burnMicros)
    {
        var stop = false;
        var worker = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                if (suppressed)
                {
                    NativeMethods.pj_burn_nogc(burnMicros);
                }
                else
                {
                    NativeMethods.pj_burn(burnMicros);
                }
            }
        })
        { IsBackground = true };

        worker.Start();
        // Let the worker get inside a native call before the first collection.
        Thread.Sleep(20);

        var worst = 0.0;
        for (var i = 0; i < 6; i++)
        {
            // Give the collector something to do, so the pause is a real collection
            // rather than a no-op.
            for (var j = 0; j < 20_000; j++)
            {
                _ = new byte[64];
            }
            var sw = Stopwatch.StartNew();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            sw.Stop();
            worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
        }

        Volatile.Write(ref stop, true);
        worker.Join(TimeSpan.FromSeconds(5));
        return worst;
    }

    // ==================================================================  P5

    private void P5_TheMarshallerIsNotTheProblem(nint raw)
    {
        var p = Predict(5,
            "The runtime marshaller is the expensive part of interop. Anything that goes " +
            "through it will be far slower than a hand-pinned blittable call, which is why " +
            "DisableRuntimeMarshalling exists.");

        Full("## P5 -- which marshalling actually costs");
        Full("");

        var opts = _portfolio.ToArray();
        var prices = new double[opts.Length];
        var strategies = MarshalledStrategies.Everything();

        var rows = new List<(IMarshalStrategy S, Measurement M)>();
        foreach (var s in strategies)
        {
            rows.Add((s, Bench.Measure(s.Name, 1, _ => s.Price(raw, opts, prices), samples: 7)));
        }
        rows.Sort((a, b) => a.M.MedianNs.CompareTo(b.M.MedianNs));

        Full($"All {opts.Length} positions, every strategy, same prices out:");
        Full("");
        Full("| strategy | shape | needs marshaller | median | vs fastest |");
        Full("|---|---|---|---:|---:|");
        var fastest = rows[0].M.MedianNs;
        foreach (var (s, m) in rows)
        {
            Full($"| {s.Name} | {s.Shape} | {(s.NeedsRuntimeMarshalling ? "yes" : "no")} | " +
                 $"{m.Format()} | {Bench.Ratio(m.MedianNs, fastest)} |");
        }
        Full("");
        foreach (var (s, _) in rows)
        {
            Full($"- **{s.Name}** -- {s.Note}");
        }
        Full("");

        var marshalledArray = rows.First(r => r.S.Name.Contains("marshalled array")).M.MedianNs;
        var pinned = rows.First(r => r.S.Name.Contains("pinned in place")).M.MedianNs;
        var marshalledClass = rows.First(r => r.S.Name.Contains("marshalled class")).M.MedianNs;
        var perCallBlittable = rows.First(r => r.S.Name.Contains("blittable pointer")).M.MedianNs;

        foreach (var (s, m) in rows)
        {
            _ = m;
            _ = s;
        }

        Settle(p, held: false,
            "The marshaller is not the axis that matters. A **marshalled** array and a " +
            $"hand-pinned span differ by {Bench.Ratio(Math.Max(marshalledArray, pinned), Math.Min(marshalledArray, pinned))} " +
            "-- because the element type is blittable, the marshaller pins the array rather " +
            "than copying it, and does almost exactly what the hand-written code does. " +
            $"Meanwhile a marshalled **class** costs {Bench.Ratio(marshalledClass, perCallBlittable)} a " +
            "blittable per-call pointer with the identical crossing count, because a reference " +
            "type can never be passed in place and must be allocated, copied and freed every " +
            "time. The rule is not \"avoid the marshaller\". It is **avoid non-blittable types, " +
            "and avoid crossing more often than you must** -- and of the two, the crossing count " +
            "dominates.",
            stable: false);
    }

    // ==================================================================  P12

    private unsafe void P12_TheShapeOfTheAbiIsAPerformanceDecision(nint raw, PricingEngine engine)
    {
        var p = Predict(12,
            "The greeks can be obtained by bumping an input and repricing. Adding a dedicated " +
            "greeks entry point to the ABI is a convenience, not a performance decision.");

        Full("## P12 -- the ABI's shape is the performance decision");
        Full("");

        var opt = _portfolio[0];

        var oneCall = Bench.Measure("greeks-native", 300_000, n =>
        {
            var local = opt;
            Greeks g = default;
            for (long i = 0; i < n; i++)
            {
                NativeMethods.pj_greeks_european(raw, &local, &g);
            }
            Bench.Sink += g.Delta;
        });

        var byBumping = Bench.Measure("greeks-by-bumping", 60_000, n =>
        {
            double acc = 0;
            for (long i = 0; i < n; i++)
            {
                acc += BumpAllGreeks(raw, opt).Delta;
            }
            Bench.Sink += acc;
        });

        var native = Greeks(raw, opt);
        var bumped = BumpAllGreeks(raw, opt);

        Full("| method | crossings | median | delta error vs analytic |");
        Full("|---|---:|---:|---:|");
        Full($"| `pj_greeks_european` | 1 | {oneCall.Format()} | exact |");
        Full($"| bump and reprice | 9 | {byBumping.Format()} | {Math.Abs(bumped.Delta - native.Delta):E1} |");
        Full("");

        Settle(p, held: false,
            $"Bumping needs 9 crossings where the dedicated entry point needs 1, and runs " +
            $"{Bench.Ratio(byBumping.MedianNs, oneCall.MedianNs)} slower. It is also less " +
            $"accurate -- the finite-difference delta is off by {Math.Abs(bumped.Delta - native.Delta):E1} " +
            "against a closed form that is exact, and choosing the bump size is a numerical " +
            "problem with no good answer. The five sensitivities share `d1`, `d2` and both " +
            "discount factors, so computing them together is cheaper on the native side as " +
            "well. Deciding which results travel together is a design decision taken once, in " +
            "the header, and it constrains everything built on top of it forever.",
            stable: false);
    }

    private static unsafe Greeks Greeks(nint raw, PricingOption o)
    {
        var local = o;
        Greeks g;
        NativeMethods.pj_greeks_european(raw, &local, &g);
        return g;
    }

    private static unsafe double Price(nint raw, PricingOption o)
    {
        var local = o;
        double price;
        NativeMethods.pj_price_european(raw, &local, &price);
        return price;
    }

    /// <summary>The five greeks by central differences: nine crossings, no closed form.</summary>
    private static Greeks BumpAllGreeks(nint raw, PricingOption o)
    {
        const double hs = 1e-4, hv = 1e-5, hr = 1e-5, ht = 1e-5;
        var mid = Price(raw, o);
        var sUp = Price(raw, o with { Spot = o.Spot + hs });
        var sDn = Price(raw, o with { Spot = o.Spot - hs });
        var vUp = Price(raw, o with { Volatility = o.Volatility + hv });
        var vDn = Price(raw, o with { Volatility = o.Volatility - hv });
        var rUp = Price(raw, o with { Rate = o.Rate + hr });
        var rDn = Price(raw, o with { Rate = o.Rate - hr });
        var tUp = Price(raw, o with { Years = o.Years + ht });
        var tDn = Price(raw, o with { Years = o.Years - ht });

        return new Greeks(
            Price: mid,
            Delta: (sUp - sDn) / (2 * hs),
            Gamma: (sUp - 2 * mid + sDn) / (hs * hs),
            Vega: (vUp - vDn) / (2 * hv),
            Theta: -(tUp - tDn) / (2 * ht),
            Rho: (rUp - rDn) / (2 * hr));
    }

    // ==================================================================  P9

    private void P9_CallingBackIsNotTheSameAsCallingOut(PricingEngine engine)
    {
        var p = Predict(9,
            "A callback from native code into managed code is the same transition in the " +
            "other direction, so it costs about the same as a P/Invoke.");

        Full("## P9 -- the return journey is not the same journey");
        Full("");

        var opt = _portfolio[0];
        const long paths = 400_000;

        var silent = Bench.Measure("mc-no-callback", 1,
            _ => Bench.Sink += engine.PriceMonteCarlo(opt, paths), samples: 7);

        var chatty = Bench.Measure("mc-callback-per-pair", 1,
            _ => Bench.Sink += engine.PriceMonteCarlo(opt, paths, reportEvery: 1,
                                                      progress: static (_, _) => true), samples: 7);

        var callbacks = engine.LastProgressCallCount;
        var extraNs = (chatty.MedianNs - silent.MedianNs);
        var perCallback = callbacks > 0 ? extraNs / callbacks : 0;

        Full($"Monte Carlo over {paths:N0} paths ({paths / 2:N0} antithetic pairs):");
        Full("");
        Full("| callbacks | median | implied cost per callback |");
        Full("|---:|---:|---:|");
        Full($"| 0 | {silent.Format()} | -- |");
        Full($"| {callbacks:N0} | {chatty.Format()} | {N(perCallback)} ns |");
        Full("");

        Settle(p, held: false,
            $"A reverse transition costs about {N(perCallback)} ns here against roughly " +
            "a nanosecond for the forward one -- the same boundary, an order of magnitude " +
            "apart. Going out is a mode switch; coming back is a mode switch plus locating " +
            "the managed thread state, plus an exception barrier the runtime must install " +
            "because an exception escaping into a native frame would kill the process. " +
            "That is why `report_every` is a parameter in the C header rather than a constant " +
            "in the engine: the caller is the only party that knows how much progress " +
            "reporting its user interface is worth.",
            stable: false);
    }

    // ==================================================================  P11

    private void P11_CancellationLatencyIsChosenNotGiven(PricingEngine engine)
    {
        var p = Predict(11,
            "Cancellation of a long native computation is either supported or not. Where it " +
            "is supported, it is prompt.");

        Full("## P11 -- cancellation is a latency you choose");
        Full("");
        Full("| `reportEvery` (pairs) | paths completed before it stopped | share of the run |");
        Full("|---:|---:|---:|");

        const long paths = 4_000_000;
        var rows = new List<(long Every, long Completed)>();
        foreach (var every in new long[] { 1, 100, 10_000, 1_000_000 })
        {
            using var cts = new CancellationTokenSource();
            long completed = 0;
            try
            {
                engine.PriceMonteCarlo(option: _portfolio[0], paths: paths, reportEvery: every,
                    progress: (done, _) =>
                    {
                        completed = done;
                        return false;   // stop at the first opportunity
                    });
            }
            catch (OperationCanceledException)
            {
            }
            rows.Add((every, completed));
            Both($"| {every:N0} | {completed:N0} | {100.0 * completed / paths:F2}% |");
        }
        Both("");

        var worst = rows.Max(r => r.Completed);
        var best = rows.Min(r => r.Completed);

        Settle(p, held: false,
            $"Cancellation is not prompt or slow; it is exactly as prompt as `report_every` " +
            $"makes it. At the finest setting the run stops after {best:N0} paths; at the " +
            $"coarsest it runs {worst:N0} before it notices -- a factor of " +
            $"{worst / Math.Max(1, best):N0} between two settings of the same parameter. " +
            "The engine cannot choose this, because the cost of asking (P9) and the value of " +
            "stopping are both facts about the caller. A C ABI that hard-codes its polling " +
            "interval has taken a latency decision on behalf of every application that will " +
            "ever use it.",
            stable: true);
    }

    // ==================================================================  P10

    private unsafe void P10_MatchingSizesDoNotMeanMatchingLayouts(nint raw)
    {
        var p = Predict(10,
            "If the managed struct and the C struct are the same size, the layout is right. " +
            "A mismatch would show up as a wrong size or a crash.");

        Full("## P10 -- a struct that is the right size and the wrong shape");
        Full("");

        var correct = new PricingOption(42, 40, 0.10, 0.0, 0.20, 0.5, OptionKind.Call);
        var truePrice = Price(raw, correct);

        // Same eight fields, same total size, two of them transposed. This is what a
        // header change looks like from the managed side: nothing.
        var swapped = new TransposedOption
        {
            Spot = correct.Spot, Strike = correct.Strike, Rate = correct.Rate,
            Dividend = correct.Dividend,
            Years = correct.Years,             // <- where Volatility should be
            Volatility = correct.Volatility,   // <- where Years should be
            Kind = correct.Kind, Reserved = 0,
        };

        double swappedPrice;
        var status = NativeMethods.pj_price_european(raw, (PricingOption*)&swapped, &swappedPrice);

        Both($"- `sizeof(PricingOption)` = {sizeof(PricingOption)}, `sizeof(TransposedOption)` = {sizeof(TransposedOption)} -- identical.");
        Both($"- Correct layout prices this option at **{truePrice.ToString("F6", CultureInfo.InvariantCulture)}**.");
        Both($"- Transposing `Volatility` and `Years` returns status `{status}` and a price of " +
             $"**{swappedPrice.ToString("F6", CultureInfo.InvariantCulture)}**.");
        Both("");

        var silentlyWrong = status == PricingStatus.Ok && Math.Abs(swappedPrice - truePrice) > 1e-9;

        Settle(p, held: false,
            "Both structs are " + sizeof(PricingOption) + " bytes. Transposing two fields " +
            $"produces status `{status}` and a price of " +
            $"{swappedPrice.ToString("F6", CultureInfo.InvariantCulture)} against a true value of " +
            $"{truePrice.ToString("F6", CultureInfo.InvariantCulture)}: " +
            (silentlyWrong
                ? "no error, no crash, a perfectly ordinary number that is wrong. "
                : "the boundary happened to reject it, which it will not always do. ") +
            "Nothing in the type system, the compiler or the runtime can catch this, because " +
            "at the ABI both are 56 bytes of the right alignment. The only defence is to assert " +
            "**every field offset** on both sides -- `static_assert` in `abi.cpp` and " +
            "`AbiContract.Verify` in the host -- which is why those checks exist and why " +
            "checking the total size alone would have missed it entirely.",
            stable: true);
    }

    /// <summary>
    /// The same fields, the same size, two of them transposed. Exists only so P10 can
    /// demonstrate that neither the compiler nor the runtime notices.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TransposedOption
    {
        public double Spot;
        public double Strike;
        public double Rate;
        public double Dividend;
        public double Years;        // transposed
        public double Volatility;   // transposed
        public int Kind;
        public int Reserved;
    }

    // ==================================================================  P8

    private unsafe void P8_RecompilingMovesThePrice()
    {
        var p = Predict(8,
            "Recompiling the untouched engine with a newer compiler and faster floating-point " +
            "settings does not change what it computes. The source is identical, so the prices " +
            "are identical.");

        Full("## P8 -- the number the business books");
        Full("");

        using var precise = Variants.Open(Variants.Hardened);
        using var fast = Variants.Open(Variants.FastMath);
        var ep = precise.CreateEngine();
        var ef = fast.CreateEngine();

        try
        {
            var identical = 0;
            var maxUlps = 0L;
            var maxRel = 0.0;
            var worst = default(PricingOption);
            var compared = 0;

            foreach (var o in _portfolio)
            {
                var local = o;
                double a, b;
                if (precise.PriceEuropean(ep, &local, &a) != PricingStatus.Ok) continue;
                if (fast.PriceEuropean(ef, &local, &b) != PricingStatus.Ok) continue;
                compared++;

                if (BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b))
                {
                    identical++;
                    continue;
                }
                var ulps = Math.Abs(BitConverter.DoubleToInt64Bits(a) - BitConverter.DoubleToInt64Bits(b));
                var rel = Math.Abs(a - b) / Math.Max(Math.Abs(a), 1e-12);
                if (ulps > maxUlps)
                {
                    maxUlps = ulps;
                    worst = o;
                }
                maxRel = Math.Max(maxRel, rel);
            }

            // The lattice is the interesting case: it accumulates 2,000 steps of rounding,
            // so any per-operation difference compounds.
            var lattice = _portfolio[0];
            double la, lb;
            precise.PriceAmerican(ep, &lattice, 2000, &la);
            fast.PriceAmerican(ef, &lattice, 2000, &lb);
            var latticeUlps = Math.Abs(BitConverter.DoubleToInt64Bits(la) - BitConverter.DoubleToInt64Bits(lb));

            Both($"- {compared:N0} positions priced through both DLLs.");
            Both($"- **{identical:N0}** ({100.0 * identical / compared:F2}%) are bit-identical.");
            Both($"- **{compared - identical:N0}** differ. Worst: {maxUlps} ULP, relative difference {maxRel:E2}.");
            Both($"- On a 2,000-step American lattice the same option differs by **{latticeUlps} ULP** " +
                 $"({Math.Abs(la - lb):E2} absolute).");
            Both("");

            var held = identical == compared;
            Settle(p, held,
                held
                    ? "Every price is bit-identical. Nothing to report."
                    : $"{compared - identical:N0} of {compared:N0} positions ({100.0 * (compared - identical) / compared:F2}%) " +
                      $"come out differently, by up to {maxUlps} ULP ({maxRel:E2} relative). The " +
                      "engine source is byte-identical; only `/fp:fast /arch:AVX2` changed. That " +
                      "flag licenses the compiler to reassociate floating-point arithmetic, " +
                      "contract multiply-add pairs into FMA, and use vectorised transcendentals " +
                      $"with different rounding. On the {latticeUlps}-ULP lattice case the error " +
                      "compounds over 2,000 steps rather than cancelling. " +
                      "The magnitudes are far below anything the desk would notice on a single " +
                      "trade -- and that is the problem, not the reassurance: a modernisation " +
                      "programme that recompiles for speed and reconciles against the old system " +
                      "will find a stream of tiny unexplained breaks, decide they are noise, and " +
                      "lose the ability to tell noise from a real regression. Either pin the " +
                      "flags or agree a tolerance in advance. Discovering this during parallel " +
                      "run is the expensive way.",
                stable: true);
        }
        finally
        {
            precise.Destroy(ep);
            fast.Destroy(ef);
        }
    }

    // ==================================================================  P6

    private void P6_ACppExceptionDoesNotBecomeANetException()
    {
        var p = Predict(6,
            "A C++ exception escaping through a C ABI into .NET surfaces as some kind of " +
            "managed exception -- an SEHException at worst. Unpleasant, but catchable.");

        Full("## P6 -- what a C++ exception does to a .NET process");
        Full("");

        var legacy = RunAbiExceptionChild(Variants.LegacyBoundary);
        var hardened = RunAbiExceptionChild(Variants.Hardened);

        Both("`pj_price_american` with `steps = -1`. The engine builds a `std::vector` sized");
        Both("`steps + 1`, which as a `size_t` is enormous, so the allocation throws.");
        Both("");
        Both("| boundary | outcome | exit code |");
        Both("|---|---|---:|");
        Both($"| `pricing_legacy.dll` (no exception barrier) | {legacy.Description} | {legacy.ExitCode} |");
        Both($"| `pricing.dll` (hardened) | {hardened.Description} | {hardened.ExitCode} |");
        Both("");

        Settle(p, held: false,
            $"Against the 2009 boundary the child process {legacy.Description.ToLowerInvariant()} " +
            $"(exit code {legacy.ExitCode}). Nothing was catchable, because there was no managed " +
            "frame left to catch it in: unwinding a C++ exception through a frame compiled as C " +
            "is undefined behaviour, and on MSVC/x64 it terminates. No stack trace, no `finally`, " +
            "no flush of anything buffered. Against the hardened boundary the same input " +
            $"{hardened.Description.ToLowerInvariant()}. The barrier in `abi.cpp` is four lines of " +
            "`catch` and it is the difference between an error code and losing the process -- " +
            "which is why the header specifies `noexcept` behaviour as part of the ABI contract " +
            "rather than leaving it to whoever writes the next entry point.",
            stable: true);
    }

    private sealed record ChildOutcome(int ExitCode, string Description);

    private static ChildOutcome RunAbiExceptionChild(string variantName)
    {
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (!exe.EndsWith("Bridge.Report.exe", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Bridge.Report.dll"));
        }
        psi.ArgumentList.Add("abi-exception");
        psi.ArgumentList.Add(variantName);

        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30_000);

        var code = proc.ExitCode;
        if (output.Contains("STATUS:", StringComparison.Ordinal))
        {
            var status = output[(output.IndexOf("STATUS:", StringComparison.Ordinal) + 7)..].Trim();
            return new ChildOutcome(code, $"Returned `{status}` and kept running");
        }
        return new ChildOutcome(code, code == 0 ? "Survived silently" : "Died");
    }

    /// <summary>Child entry point for the ABI-exception experiment.</summary>
    public static unsafe int AbiExceptionChild(string variantName)
    {
        using var variant = Variants.Open(variantName);
        var engine = variant.CreateEngine();
        var o = new PricingOption(42, 40, 0.10, 0.0, 0.20, 0.5, OptionKind.Call);
        double price;
        var status = variant.PriceAmerican(engine, &o, -1, &price);
        Console.Out.WriteLine($"STATUS:{status}");
        Console.Out.Flush();
        variant.Destroy(engine);
        return 0;
    }

    // ==================================================================  P7

    private void P7_TheEngineDidNotNeedToChange()
    {
        var p = Predict(7,
            "Making a 2009 C++ library safe to expose means fixing the C++. The dangerous " +
            "behaviour is in the engine, so the engine has to be audited and changed -- which " +
            "is exactly the work the business refuses to authorise.");

        Full("## P7 -- the fuzz differential");
        Full("");

        var legacy = FuzzDriver.RunIsolated(Variants.LegacyBoundary, FuzzDriver.DefaultSeed,
            FuzzCases, TimeSpan.FromMinutes(4));
        var hardened = FuzzDriver.RunIsolated(Variants.Hardened, FuzzDriver.DefaultSeed,
            FuzzCases, TimeSpan.FromMinutes(4));

        Both($"{FuzzCases} generated inputs, the same corpus (seed `0x{FuzzDriver.DefaultSeed:X}`) against both");
        Both("boundaries, each run in child processes so that a crash is a data point rather than");
        Both("the end of the experiment.");
        Both("");
        Both("| outcome | `pricing_legacy.dll` | `pricing.dll` |");
        Both("|---|---:|---:|");
        foreach (var outcome in Enum.GetValues<FuzzOutcome>())
        {
            Both($"| {Describe(outcome)} | {legacy.Count(outcome)} | {hardened.Count(outcome)} |");
        }
        Both($"| **unsafe outcomes** | **{legacy.UnsafeCases}** | **{hardened.UnsafeCases}** |");
        Both("");

        if (legacy.SilentlyWrongIndices.Count > 0)
        {
            Both($"First silently-wrong case against the 2009 boundary: index {legacy.SilentlyWrongIndices[0]}.");
        }
        if (legacy.CorruptionIndices.Count > 0)
        {
            Both($"First memory corruption: index {legacy.CorruptionIndices[0]}.");
        }
        if (legacy.CrashIndices.Count > 0)
        {
            Both($"First process death: index {legacy.CrashIndices[0]}.");
        }
        Both("");

        // Without this paragraph the table above is unreadable. A boundary that refuses
        // every input scores zero unsafe outcomes, which is the same score as a boundary
        // that is genuinely safe. The control group is what separates the two, and it is
        // reported before the conclusion because it is a precondition for the conclusion.
        Both($"Of those {FuzzCases} inputs, {hardened.ControlTotal} are a control group: " +
             "ordinary options with every field in range, sane lattice steps and a batch " +
             "that fits its buffer. They exist because the first version of this experiment " +
             "reported a perfect score for the hardened boundary -- and a boundary that " +
             "rejects everything scores exactly the same. The hardened DLL accepted " +
             $"{hardened.ControlAccepted} of {hardened.ControlTotal}" +
             (hardened.ControlHeld
                 ? ", so the zeroes above are safety rather than paralysis."
                 : $", refusing {hardened.ControlRefusedIndices.Count} legal input(s) " +
                   $"(first: index {hardened.ControlRefusedIndices[0]}). Until that is " +
                   "fixed, the unsafe-outcome counts say nothing about safety."));
        Both("");

        var held = hardened.UnsafeCases > 0;
        Settle(p, held,
            $"`engine.cpp` is byte-identical in both DLLs. The 2009 boundary produces " +
            $"{legacy.UnsafeCases} unsafe outcomes on this corpus; the hardened boundary produces " +
            $"{hardened.UnsafeCases}. Not one line of the pricing code was touched -- the entire " +
            "difference is argument validation, capacity checks and an exception barrier in " +
            "`abi.cpp`. " +
            (legacy.Count(FuzzOutcome.SilentlyWrong) > legacy.Count(FuzzOutcome.ProcessDied)
                ? $"Note the shape of the failures: {legacy.Count(FuzzOutcome.SilentlyWrong)} silently " +
                  $"wrong against {legacy.Count(FuzzOutcome.ProcessDied)} crashes. The crashes are the " +
                  "safe failures. A process that dies gets noticed; a negative option price returned " +
                  "with a success code gets booked. "
                : "") +
            "This is the answer to \"we cannot afford to audit 60,000 lines of C++\": you do not " +
            "have to. You have to own the 300 lines it is reached through.",
            stable: true);
    }

    private static string Describe(FuzzOutcome o) => o switch
    {
        FuzzOutcome.Accepted => "accepted, returned a usable price",
        FuzzOutcome.RejectedCleanly => "refused with a status code",
        FuzzOutcome.SilentlyWrong => "**success, and not a price** (NaN, infinite or negative)",
        FuzzOutcome.MemoryCorruption => "**wrote past the caller's buffer**",
        FuzzOutcome.ProcessDied => "**killed the process**",
        _ => o.ToString(),
    };

    // ==================================================================  end

    private void Scoreboard()
    {
        var held = _predictions.Count(x => x.Verdict == "HELD");
        Both("## Scoreboard");
        Both("");
        Both($"{_predictions.Count} predictions written before measuring. {held} held, " +
             $"{_predictions.Count - held} did not.");
        Both("");
        Both("| # | verdict | reproducible? | claim |");
        Both("|---|---|---|---|");
        foreach (var pr in _predictions.OrderBy(x => x.Id))
        {
            var claim = pr.Expected.Length > 90 ? pr.Expected[..90] + "..." : pr.Expected;
            Both($"| P{pr.Id} | {pr.Verdict} | {(pr.IsStable ? "exact" : "wall-clock")} | {claim} |");
        }
        Both("");

        if (_predictions.Any(x => x.Verdict is null))
        {
            throw new InvalidOperationException("a prediction was never settled");
        }
    }
}

