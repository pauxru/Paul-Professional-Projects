# 4. What the tests caught

Four defects in my own hardened boundary, one in the harness, and one hole in the test
suite that only mutation testing could find.

The boundary is the thing this project claims is safe. Writing tests for it found four
ways it was not, and the most interesting one was not a safety hole at all.

---

## 1. It refused the most routine input in the book

The hardened boundary rejected `volatility <= 0` and `years <= 0` as out of domain. Both
look obviously correct. A negative volatility is meaningless, a negative time to expiry is
meaningless, refuse them.

But the check was `<=`, and **every option expiring today has `years == 0`**. On every
expiry date. For every strike. On the third Friday of every month, the entire expiring
book hits this path.

This is the same failure the control group was added to detect (ADR-002), sitting in the
production code rather than in the experiment. A boundary whose safety comes from refusing
legal input is not safe. It is unusable, and it will be switched off -- not by a careless
engineer, but by a competent one at 4pm on expiry day who has correctly worked out that
the boundary is the problem.

The fix is `degenerate_price()`: the hardened boundary takes the limit at the seam --
discounted intrinsic off the forward -- and never calls the 2009 core for those inputs.
The core returns NaN for them. **The hardened boundary is now more correct than the engine
it protects**, which was not the plan and is the more interesting result. The boundary is
where you can afford to be careful, precisely because it is the part you are allowed to
change.

Greeks still refuse, explicitly and with a reason. Theta is unbounded at expiry; gamma at
zero volatility is a delta function. Refusing an answer that does not exist is safety.
Refusing one that does is breakage. The distinction is the whole of it.

## 2. Every rejection said the same thing

`option_is_sane()` returned `bool`. So every one of six possible defects produced the
message *"option failed domain validation at the ABI boundary"*.

The boundary was safe and useless. A desk with a rejected trade at 4pm has a 56-byte
struct and no idea which field is wrong; they will bisect it by hand, or more likely they
will call someone. The check prevented a bad price and created a support ticket, and over
a year the support tickets cost more than the bad price would have.

Replaced with `option_defect()`, returning `const char*` -- `nullptr` if fine, otherwise
the name of the field. "volatility must not be negative" is a message somebody can act on
without calling anybody.

## 3. `strike = 1e300` returned exactly `0.0`

Every finiteness check passed. Every domain check passed. Strike is positive and finite,
spot is positive and finite, volatility and time are ordinary.

The answer was `0.0`. Finite. Non-negative. Entirely plausible for a deeply
out-of-the-money option. And corrupt.

This is `SilentlyWrong` -- the category this project's own taxonomy ranks as worse than
memory corruption -- occurring inside the boundary written to eliminate it. Fixed with a
relative magnitude check (`kMaxMoneyness = 1e12`) in both directions, verified not to be a
false positive: `strike = 42e6` against `spot = 42` still prices, at 0.0, because at that
moneyness zero is the right answer.

The general lesson is that finiteness is not sanity. `1e300` is a perfectly good double.
Validation that checks each field in isolation cannot see a *relationship* between fields
that makes no sense.

## 4. A double-free in the harness

`NativeVariant.Dispose()`:

```csharp
public void Dispose()
{
    if (_module != nint.Zero)      // never assigned zero
    {
        NativeLibrary.Free(_module);
    }
}
```

The guard tested a field that was never cleared, so it caught nothing. The second
`Dispose` called `Free` on a handle the loader had already released and threw
`InvalidOperationException` out of a `using` block, replacing whatever the caller was
doing with a stack trace about interop.

Two things about this are worth keeping.

**Nobody wrote `Free(h); Free(h);`.** There was a `using var variant` in the caller and a
`FuzzRunner` that also implemented `IDisposable` over the same variant. Each was
individually correct. Two objects, each correctly releasing what each believed it owned --
which is what every real double-free looks like. The fix was not just the guard; it was
removing `IDisposable` from `FuzzRunner`, so the compiler now rejects the call that caused
it. Borrowing is the right relationship, and it should be expressible.

**It was intermittent in the direction that hides it.** The OS loader reference-counts, so
the second free only fails when no other load of the same DLL is outstanding -- meaning
the bug appears when a test runs *alone* and vanishes when it runs with the suite.
Filtering down to reproduce it is the natural debugging move, and it is the move that
makes it disappear.

## 5. The mutation that survived

Stage 6 of `test.ps1` deletes one check from `abi.cpp`, rebuilds the native DLLs, and
re-runs the suite. Six mutations. Five died. One lived:

```cpp
if (out_capacity < count) {        →    if (out_capacity < 0) {
```

Deleting the batch capacity check -- the check whose absence *is* the 63-slot heap
overwrite, one of the project's three headline findings -- left all 177 tests green.

There was a test for it. `An_output_buffer_smaller_than_the_batch_is_refused_before_the_call`
asserts an `ArgumentException` from `PricingEngine.PriceBatch`. It is a good test, it is
correctly named, and it passes just as happily when the native boundary has no check at
all, because the exception is thrown in managed code before the P/Invoke ever happens.

**It was a test of the wrapper wearing the name of a test of the boundary.**

That distinction is not academic. Real callers of a C ABI do not go through
`PricingEngine`. They are Python, Excel, another C++ program, a decade-old VB6 front end,
and whatever the next team writes. The managed wrapper is the cheapest place to stop a bad
call; it is not the place that protects anyone.

The replacement calls `pj_price_batch` directly with a guard-filled over-allocation and
asserts both halves of the contract: status `Capacity`, **and** zero guard slots
overwritten. Its counterpart asserts the 2009 boundary returns `Ok` and overwrites all 63.
A third asserts a buffer that exactly fits is not treated as short, because a capacity
check with the wrong comparison would refuse the most common call there is.

This gap is invisible to coverage -- the line *is* covered, by a call that never reaches
it -- and invisible to review, because the test looks right at the level it operates. The
only thing that finds it is deleting the check and seeing whether anybody notices.

## 6. A convergence test that asserted something false

The CRR lattice test asserted that successive doublings of the step count get closer
together, within a fixed parity class. Parity matters because even and odd step counts
approach the limit from opposite sides, depending on whether a node lands on the strike.

Holding parity fixed, the gaps are:

```
1.02e-3, 6.70e-4, 6.93e-4, 4.48e-5, 6.20e-5
```

Decaying overall. Not monotone -- the third is larger than the second.

Parity was a proxy for the wrong thing. The quantity that oscillates is not `N mod 2` but
how close the nearest lattice node sits to the strike, and that distance wanders as the
node spacing `sigma*sqrt(T/N)` changes. So the honest claims are the two that survive the
wobble: the gap stays under an O(1/N) envelope, and it shrinks across *two* doublings even
where it grows across one.

Both are testable without knowing the answer, which is necessary here -- the value quoted
in the literature for this option is 4.478, and this lattice converges to about 4.487.
Chasing that discrepancy is how the test ended up being written properly. A test that
asserts a published constant would have been failing for a reason that has nothing to do
with the code.

## What all six have in common

Five of the six were found by a test written to *demonstrate* something, not to check it.
The degenerate-input defect surfaced while writing a test about what happens at expiry.
The generic error message surfaced while asserting on a message. The moneyness hole
surfaced while building the silently-wrong contrast.

The sixth was found by deliberately breaking the code and checking whether anyone
complained -- which is the only technique here that can find a defect nobody thought to
look for.

A test suite that only encodes what its author already believed is a suite that agrees
with its author. The two things that broke that pattern in this project were a control
group and a mutation stage: one asks *"would this notice if the system did nothing?"*, the
other asks *"would this notice if the system stopped protecting anyone?"*. Both questions
have to be asked from outside the suite, because a suite cannot ask them of itself.
