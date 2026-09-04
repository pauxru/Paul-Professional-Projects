# 2. How it is built

Three DLLs, two host assemblies, and a report that has to survive being regenerated.

---

## Three DLLs from one source file

```
native/
  include/pricing_abi.h   the C ABI: 9 entry points, static_asserts on every offset
  src/engine.hpp          the crown jewels, declared
  src/engine.cpp          the crown jewels. Byte-identical in all three DLLs.
  src/abi.cpp             the boundary. #if PJ_HARDENED is the entire experiment.
  tests/selftest.cpp      129 checks, no CLR in the process
```

CMake builds `pricing`, `pricing_legacy` and `pricing_fast` from the same source list.
They differ in two ways and only two:

| target | `PJ_HARDENED` | floating point |
|---|---|---|
| `pricing` | 1 | `/fp:precise` |
| `pricing_legacy` | 0 | `/fp:precise` |
| `pricing_fast` | 1 | `/fp:fast /arch:AVX2` |

Everything `PJ_HARDENED` controls lives in `abi.cpp`, inside `#if` blocks. `engine.cpp`
contains no conditional compilation at all, and never will -- ADR-001 is about the two
occasions where fixing the engine was clearly the better code and was rejected anyway.

Stage 1 of `test.ps1` hashes all three binaries and fails if any two match. A broken
variant target that emits one DLL three times would make every comparison in the report a
tautology reporting perfect agreement, and it would look like a spectacular result.

## The boundary

Nine entry points. Each one, in the hardened build, does the same five things in the same
order:

```cpp
PJ_API pj_status PJ_CALL pj_price_american(pj_engine* engine, const pj_option* opt,
                                           int32_t steps, double* out_price) {
    auto* e = reinterpret_cast<contoso::Engine*>(engine);
#if PJ_HARDENED
    if (e == nullptr || opt == nullptr || out_price == nullptr) return PJ_ERR_NULL_ARG;
    *out_price = 0.0;
    if (const char* defect = option_defect(*opt)) { set_error(e, defect); return PJ_ERR_BAD_ARG; }
    if (steps < 1)          { set_error(e, "steps must be at least 1"); return PJ_ERR_BAD_ARG; }
    if (steps > kMaxSteps)  { set_error(e, "steps exceeds the supported lattice size"); return PJ_ERR_BAD_ARG; }
#endif
    PJ_TRY
        const double p = contoso::crr_american(*opt, steps);
#if PJ_HARDENED
        if (!std::isfinite(p)) { set_error(e, "lattice produced a non-finite price"); return PJ_ERR_NOT_CONVERGED; }
#endif
        *out_price = p;
        return PJ_OK;
    PJ_CATCH(e)
}
```

Null checks, domain validation, bounds, the call, an output check. `PJ_TRY`/`PJ_CATCH` is
the exception barrier: unwinding a C++ exception through a frame compiled as C is
undefined behaviour, and on MSVC/x64 it terminates the process. Measured, exit code
`-1073741819`, nothing catchable, because there is no managed frame left to catch it in.

`option_defect()` returns the name of the field that was wrong, not a `bool`. The first
version returned `bool`, and every rejection read *"option failed domain validation at the
ABI boundary"*. A boundary that declines cleanly but will not say which of six fields was
at fault has moved the problem to the support queue, not solved it.

## `degenerate_price()`, and why pricing code lives in the boundary

The hardened build originally refused `volatility == 0` and `years == 0` as out of
domain. Both refusals were wrong, and the second one was seriously wrong: **every option
expiring today has `years == 0`**, on every expiry date, for every strike. A boundary
whose safety comes from refusing legal input is not safe. It is unusable, and it will be
switched off by the first person who has a deadline.

The obvious fix is three lines in `black_scholes()` handling the limit. It would have
been correct, smaller, and in the right place by every ordinary standard.

It was rejected, because the moment `engine.cpp` differs between builds, *"the danger is
in the boundary, not the engine"* stops being a measurement and becomes an assertion. So
the limit is taken at the seam instead, in `abi.cpp`, and the core is never called for
those inputs. The hardened boundary now returns the correct discounted intrinsic where
the 2009 core returns NaN -- it is **more correct**, not merely stricter.

Greeks still refuse, explicitly. Theta is unbounded at expiry and gamma at zero
volatility is a delta function. Refusing an answer that does not exist is safety.
Refusing one that does is breakage.

## Why the host is two assemblies

```xml
<DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>
```

This is the fast path: blittable arguments go straight through, no stub, no per-call
signature work. It is also assembly-wide, and it disables **`SafeHandle` marshalling**
along with everything else -- because `SafeHandle`'s reference counting is implemented by
the marshaller, in the generated stub, not by the type.

So the assembly that wants the fast calling convention cannot use the type that makes
handle lifetime safe. Those are the two things the project most wanted together.

The resolution is a split. `Bridge.Core` sets the switch, keeps the `SafeHandle`, and
hand-writes what the marshaller would have generated:

```csharp
var added = false;
try
{
    handle.DangerousAddRef(ref added);
    status = NativeMethods.pj_price_european(handle.DangerousGetHandle(), &o, &price);
}
finally
{
    if (added) handle.DangerousRelease();
}
```

`Bridge.Legacy` does not set the switch and holds its DLLs by raw `nint` on purpose. Its
job is to be fuzzed; a wrapper that prevented the dangerous observation would defeat the
experiment.

A generic `WithHandle<T>` helper to remove the ceremony was written and does not compile:
the call sites need the address of a local (`&price`), and C# forbids taking the address
of a local captured by a lambda -- `CS1686`. Making it work needs fields or heap
allocation, which is a worse trade on a path whose entire purpose is to have no per-call
cost.

## Struct layout is a security control

`PricingOption` is 56 bytes on both sides. Transposing `Volatility` and `Years` gives
status `Ok` and a price of 5.179541 against a true 4.759422.

No error. No crash. An ordinary number that is wrong.

Nothing in the type system, the compiler or the runtime can catch this, because at the
ABI both structs are 56 bytes of the right alignment. Checking `sizeof` -- which is the
check most people write -- would have missed it entirely.

So every field offset is asserted on both sides: `static_assert(offsetof(...))` in
`abi.cpp`, and `AbiContract.Verify()` in the host, run from a static constructor before
any call can happen. (`[ModuleInitializer]` would be the natural home and is forbidden in
a library -- `CA2255`.) A `reserved` field that must be zero catches the other direction:
a caller whose struct grew a field this build does not know about.

## The report is the product

`Bridge.Report` writes `docs/results.md`. Twelve predictions, written down before
anything was measured, each settled by an experiment.

It writes two files. `results.md` has everything including timings; `results-stable.md`
has only claims that are exactly reproducible, and where a prediction's evidence is a
wall-clock number the stable file says so rather than omitting it. `test.ps1`
byte-compares the stable file only.

That split is not tidiness. A byte-comparison of a file containing "43.2 ns" fails
immediately and forever, and the alternatives -- rounding, or comparing some lines -- both
produce a check that passes on one machine and fails on another, which teaches the team
to ignore it. Reproducibility is a property of each claim, not of the document.

Eleven of the twelve predictions were contradicted.

---

*Next: [the argument](03-the-argument.md) -- what the measurements actually establish,
and what they do not.*
