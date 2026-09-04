# Crossing the boundary: what interop actually costs, and what it actually risks

A 60,000-line C++ pricing engine, unchanged, exposed through a narrow C ABI and
called from .NET. Twelve claims about that boundary, each written down before it
was measured.

Three DLLs are built from **identical** `engine.cpp`:

| DLL | boundary (`abi.cpp`) | floating point |
|---|---|---|
| `pricing.dll` | hardened: validates, bounds, catches | `/fp:precise` |
| `pricing_legacy.dll` | as found in 2009: trusts everything | `/fp:precise` |
| `pricing_fast.dll` | hardened | `/fp:fast /arch:AVX2` |

Because the engine is byte-identical in all three, every difference measured
below belongs either to the boundary or to the compiler, and never to someone
quietly improving the maths.

Timings are medians of 9 samples after 3 warm-up rounds, on one shared
developer machine. They are reported to the precision they deserve, which is
about one significant figure for a ratio and none at all for an absolute
number quoted out of context.

## P1 -- the cost of a crossing

| call | median | boundary overhead as a share of the call |
|---|---:|---:|
| managed method (nothing crosses) | 2.6 ns | -- |
| `pj_noop` (crossing, no work) | 2.6 ns | 100% |
| `pj_price_european` (~200ns of work) | 63.9 ns | 4% |
| `pj_price_american(512)` (~1ms of work) | 2.76 ms | 0.00% |

**P1 -- expected.** A P/Invoke costs a few tens of nanoseconds. That is a rounding error next to any real computation, so the boundary can be treated as free and the API designed for clarity instead.

**P1 -- CONTRADICTED.** The crossing itself costs 2.6 ns, which is indeed small. But it is not a rounding error, it is a **ratio**. Against `pj_price_european` it is 4% of the call; against `pj_price_american(512)` it is 0.00%. The same boundary is 43193x more expensive for one function than the other, and neither number is a property of the boundary. The design rule that follows is not "P/Invoke is cheap" but **put enough work behind each crossing that the crossing stops mattering** -- which is a statement about API shape, not about performance tuning.

## P2 -- per-call versus batch

Pricing all 4000 positions:

| shape | crossings | median |
|---|---:|---:|
| one call per option | 4000 | 287.20 us |
| one call for the book | 1 | 252.50 us |

**P2 -- expected.** Batching a portfolio into one call instead of N saves the per-call overhead, so the win is bounded by that overhead: somewhere between 2x and 5x.

**P2 -- HELD.** Batching is 1.14x faster, not 2-5x. The prediction accounted for the transition and forgot everything around it: the per-call shape pays a stub, an argument setup, a return-value check and a status branch for every single option, and the loop that drives it never gets to stay inside native code long enough to warm anything. Batching removes 3999 crossings and, more importantly, moves the loop to the side of the boundary where the data already is.

## P3 -- what safety costs

| path | median | delta |
|---|---:|---:|
| raw pointer, no reference count | 62.6 ns | -- |
| `PricingEngine.PriceEuropean` (SafeHandle AddRef/Release, try/finally, status check) | 78.9 ns | +16.343 ns (26%) |

**P3 -- expected.** SafeHandle's reference counting is a pair of interlocked operations. Next to a P/Invoke that is free, so the safe wrapper costs nothing measurable.

**P3 -- CONTRADICTED.** The safe path costs +16.343 ns per call, 26% on top of the raw one. Whether that is free depends entirely on the shape of the API above it: on the per-call path it is a real fraction of every price, and on the batch path it is paid once for 4000 options and disappears. The conclusion is not "SafeHandle is expensive" -- it is that a chatty API makes safety look expensive, and the fix is the API, because the alternative is to buy back a few nanoseconds by making use-after-free representable.

## P4 -- SuppressGCTransition, and what it costs somebody else

On an empty call: 2.6 ns with the transition, 2.1 ns without -- 1.26x faster.

So far the prediction looks right. Now the same attribute on a call that is not short:

| native call in flight on another thread | longest `GC.Collect()` observed |
|---|---:|
| `pj_burn(60ms)`, normal transition | 1.2 ms |
| `pj_burn(60ms)`, SuppressGCTransition | 52.3 ms |

**P4 -- expected.** SuppressGCTransition removes work from every call and changes nothing else, so it should be applied to every short native call as a matter of course.

**P4 -- CONTRADICTED.** The speed-up is real -- 1.26x on an empty call, and it comes from removing the switch to preemptive GC mode. That switch is what allows the runtime to suspend the calling thread. Remove it and the thread is uninterruptible for the duration of the call, so a collection anywhere else in the process waits for it: a `GC.Collect()` on another thread ran in 1.2 ms while a normal 60 ms native call was in flight, and 52.3 ms while a suppressed one was -- the collector waited for the native call to finish. The attribute is not an optimisation you apply to short calls; it is a promise that the call is short, made to a component that has no way to check.

## P5 -- which marshalling actually costs

All 4000 positions, every strategy, same prices out:

| strategy | shape | needs marshaller | median | vs fastest |
|---|---|---|---:|---:|
| batch, pinned in place | batch | no | 251.20 us | 1.00x |
| batch, marshalled array | batch | yes | 261.00 us | 1.04x |
| per-call, blittable pointer | per-call | no | 284.80 us | 1.13x |
| per-call, SuppressGCTransition | per-call | no | 288.20 us | 1.15x |
| batch, hand-copied to unmanaged memory | batch | no | 368.20 us | 1.47x |
| per-call, marshalled class | per-call | yes | 438.80 us | 1.75x |
| per-call, raw function pointer | per-call | no | 551.10 us | 2.2x |

- **batch, pinned in place** -- 1 crossing, zero copies. The managed heap IS the native buffer.
- **batch, marshalled array** -- 1 crossing; the marshaller pins rather than copies because the element type is blittable.
- **per-call, blittable pointer** -- N crossings, zero copies. The cost of the boundary, undiluted.
- **per-call, SuppressGCTransition** -- N crossings without the preemptive-mode switch. Safe only for bounded calls.
- **batch, hand-copied to unmanaged memory** -- 1 crossing, 2 allocations and 2N copies. Correct, and needless.
- **per-call, marshalled class** -- N crossings plus N unmanaged allocations, copies and frees.
- **per-call, raw function pointer** -- N indirect calls, no stub and no reference count. The floor, and unsafe.

**P5 -- expected.** The runtime marshaller is the expensive part of interop. Anything that goes through it will be far slower than a hand-pinned blittable call, which is why DisableRuntimeMarshalling exists.

**P5 -- CONTRADICTED.** The marshaller is not the axis that matters. A **marshalled** array and a hand-pinned span differ by 1.04x -- because the element type is blittable, the marshaller pins the array rather than copying it, and does almost exactly what the hand-written code does. Meanwhile a marshalled **class** costs 1.54x a blittable per-call pointer with the identical crossing count, because a reference type can never be passed in place and must be allocated, copied and freed every time. The rule is not "avoid the marshaller". It is **avoid non-blittable types, and avoid crossing more often than you must** -- and of the two, the crossing count dominates.

## P12 -- the ABI's shape is the performance decision

| method | crossings | median | delta error vs analytic |
|---|---:|---:|---:|
| `pj_greeks_european` | 1 | 137.5 ns | exact |
| bump and reprice | 9 | 621.3 ns | 1.9E-011 |

**P12 -- expected.** The greeks can be obtained by bumping an input and repricing. Adding a dedicated greeks entry point to the ABI is a convenience, not a performance decision.

**P12 -- CONTRADICTED.** Bumping needs 9 crossings where the dedicated entry point needs 1, and runs 4.5x slower. It is also less accurate -- the finite-difference delta is off by 1.9E-011 against a closed form that is exact, and choosing the bump size is a numerical problem with no good answer. The five sensitivities share `d1`, `d2` and both discount factors, so computing them together is cheaper on the native side as well. Deciding which results travel together is a design decision taken once, in the header, and it constrains everything built on top of it forever.

## P9 -- the return journey is not the same journey

Monte Carlo over 400,000 paths (200,000 antithetic pairs):

| callbacks | median | implied cost per callback |
|---:|---:|---:|
| 0 | 17.02 ms | -- |
| 200,000 | 18.60 ms | 7.892 ns |

**P9 -- expected.** A callback from native code into managed code is the same transition in the other direction, so it costs about the same as a P/Invoke.

**P9 -- CONTRADICTED.** A reverse transition costs about 7.892 ns here against roughly a nanosecond for the forward one -- the same boundary, an order of magnitude apart. Going out is a mode switch; coming back is a mode switch plus locating the managed thread state, plus an exception barrier the runtime must install because an exception escaping into a native frame would kill the process. That is why `report_every` is a parameter in the C header rather than a constant in the engine: the caller is the only party that knows how much progress reporting its user interface is worth.

## P11 -- cancellation is a latency you choose

| `reportEvery` (pairs) | paths completed before it stopped | share of the run |
|---:|---:|---:|
| 1 | 2 | 0.00% |
| 100 | 200 | 0.01% |
| 10,000 | 20,000 | 0.50% |
| 1,000,000 | 2,000,000 | 50.00% |

**P11 -- expected.** Cancellation of a long native computation is either supported or not. Where it is supported, it is prompt.

**P11 -- CONTRADICTED.** Cancellation is not prompt or slow; it is exactly as prompt as `report_every` makes it. At the finest setting the run stops after 2 paths; at the coarsest it runs 2,000,000 before it notices -- a factor of 1,000,000 between two settings of the same parameter. The engine cannot choose this, because the cost of asking (P9) and the value of stopping are both facts about the caller. A C ABI that hard-codes its polling interval has taken a latency decision on behalf of every application that will ever use it.

## P10 -- a struct that is the right size and the wrong shape

- `sizeof(PricingOption)` = 56, `sizeof(TransposedOption)` = 56 -- identical.
- Correct layout prices this option at **4.759422**.
- Transposing `Volatility` and `Years` returns status `Ok` and a price of **5.179541**.

**P10 -- expected.** If the managed struct and the C struct are the same size, the layout is right. A mismatch would show up as a wrong size or a crash.

**P10 -- CONTRADICTED.** Both structs are 56 bytes. Transposing two fields produces status `Ok` and a price of 5.179541 against a true value of 4.759422: no error, no crash, a perfectly ordinary number that is wrong. Nothing in the type system, the compiler or the runtime can catch this, because at the ABI both are 56 bytes of the right alignment. The only defence is to assert **every field offset** on both sides -- `static_assert` in `abi.cpp` and `AbiContract.Verify` in the host -- which is why those checks exist and why checking the total size alone would have missed it entirely.

## P8 -- the number the business books

- 4,000 positions priced through both DLLs.
- **1,182** (29.55%) are bit-identical.
- **2,818** differ. Worst: 602 ULP, relative difference 6.68E-014.
- On a 2,000-step American lattice the same option differs by **3 ULP** (5.33E-015 absolute).

**P8 -- expected.** Recompiling the untouched engine with a newer compiler and faster floating-point settings does not change what it computes. The source is identical, so the prices are identical.

**P8 -- CONTRADICTED.** 2,818 of 4,000 positions (70.45%) come out differently, by up to 602 ULP (6.68E-014 relative). The engine source is byte-identical; only `/fp:fast /arch:AVX2` changed. That flag licenses the compiler to reassociate floating-point arithmetic, contract multiply-add pairs into FMA, and use vectorised transcendentals with different rounding. On the 3-ULP lattice case the error compounds over 2,000 steps rather than cancelling. The magnitudes are far below anything the desk would notice on a single trade -- and that is the problem, not the reassurance: a modernisation programme that recompiles for speed and reconciles against the old system will find a stream of tiny unexplained breaks, decide they are noise, and lose the ability to tell noise from a real regression. Either pin the flags or agree a tolerance in advance. Discovering this during parallel run is the expensive way.

## P6 -- what a C++ exception does to a .NET process

`pj_price_american` with `steps = -1`. The engine builds a `std::vector` sized
`steps + 1`, which as a `size_t` is enormous, so the allocation throws.

| boundary | outcome | exit code |
|---|---|---:|
| `pricing_legacy.dll` (no exception barrier) | Died | -1073741819 |
| `pricing.dll` (hardened) | Returned `BadArgument` and kept running | 0 |

**P6 -- expected.** A C++ exception escaping through a C ABI into .NET surfaces as some kind of managed exception -- an SEHException at worst. Unpleasant, but catchable.

**P6 -- CONTRADICTED.** Against the 2009 boundary the child process died (exit code -1073741819). Nothing was catchable, because there was no managed frame left to catch it in: unwinding a C++ exception through a frame compiled as C is undefined behaviour, and on MSVC/x64 it terminates. No stack trace, no `finally`, no flush of anything buffered. Against the hardened boundary the same input returned `badargument` and kept running. The barrier in `abi.cpp` is four lines of `catch` and it is the difference between an error code and losing the process -- which is why the header specifies `noexcept` behaviour as part of the ABI contract rather than leaving it to whoever writes the next entry point.

## P7 -- the fuzz differential

600 generated inputs, the same corpus (seed `0xC0FFEE`) against both
boundaries, each run in child processes so that a crash is a data point rather than
the end of the experiment.

| outcome | `pricing_legacy.dll` | `pricing.dll` |
|---|---:|---:|
| accepted, returned a usable price | 272 | 154 |
| refused with a status code | 0 | 446 |
| **success, and not a price** (NaN, infinite or negative) | 263 | 0 |
| **wrote past the caller's buffer** | 7 | 0 |
| **killed the process** | 58 | 0 |
| **unsafe outcomes** | **328** | **0** |

First silently-wrong case against the 2009 boundary: index 1.
First memory corruption: index 4.
First process death: index 0.

Of those 600 inputs, 150 are a control group: ordinary options with every field in range, sane lattice steps and a batch that fits its buffer. They exist because the first version of this experiment reported a perfect score for the hardened boundary -- and a boundary that rejects everything scores exactly the same. The hardened DLL accepted 150 of 150, so the zeroes above are safety rather than paralysis.

**P7 -- expected.** Making a 2009 C++ library safe to expose means fixing the C++. The dangerous behaviour is in the engine, so the engine has to be audited and changed -- which is exactly the work the business refuses to authorise.

**P7 -- CONTRADICTED.** `engine.cpp` is byte-identical in both DLLs. The 2009 boundary produces 328 unsafe outcomes on this corpus; the hardened boundary produces 0. Not one line of the pricing code was touched -- the entire difference is argument validation, capacity checks and an exception barrier in `abi.cpp`. Note the shape of the failures: 263 silently wrong against 58 crashes. The crashes are the safe failures. A process that dies gets noticed; a negative option price returned with a success code gets booked. This is the answer to "we cannot afford to audit 60,000 lines of C++": you do not have to. You have to own the 300 lines it is reached through.

## Scoreboard

12 predictions written before measuring. 1 held, 11 did not.

| # | verdict | reproducible? | claim |
|---|---|---|---|
| P1 | CONTRADICTED | wall-clock | A P/Invoke costs a few tens of nanoseconds. That is a rounding error next to any real comp... |
| P2 | HELD | wall-clock | Batching a portfolio into one call instead of N saves the per-call overhead, so the win is... |
| P3 | CONTRADICTED | wall-clock | SafeHandle's reference counting is a pair of interlocked operations. Next to a P/Invoke th... |
| P4 | CONTRADICTED | wall-clock | SuppressGCTransition removes work from every call and changes nothing else, so it should b... |
| P5 | CONTRADICTED | wall-clock | The runtime marshaller is the expensive part of interop. Anything that goes through it wil... |
| P6 | CONTRADICTED | exact | A C++ exception escaping through a C ABI into .NET surfaces as some kind of managed except... |
| P7 | CONTRADICTED | exact | Making a 2009 C++ library safe to expose means fixing the C++. The dangerous behaviour is ... |
| P8 | CONTRADICTED | exact | Recompiling the untouched engine with a newer compiler and faster floating-point settings ... |
| P9 | CONTRADICTED | wall-clock | A callback from native code into managed code is the same transition in the other directio... |
| P10 | CONTRADICTED | exact | If the managed struct and the C struct are the same size, the layout is right. A mismatch ... |
| P11 | CONTRADICTED | exact | Cancellation of a long native computation is either supported or not. Where it is supporte... |
| P12 | CONTRADICTED | wall-clock | The greeks can be obtained by bumping an input and repricing. Adding a dedicated greeks en... |

