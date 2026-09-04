# ADR-007: mutation testing targets `abi.cpp` only, and it found a real hole

**Status:** accepted
**Date:** after the first mutation run came back 5/6

## Context

The headline claim is "328 unsafe outcomes against the 2009 boundary, 0 against the
hardened one". That claim rests entirely on the test suite being able to *notice* when
the boundary stops guarding. A suite of 180 passing tests is not evidence of that. It is
evidence that 180 assertions currently hold, which is a different statement.

Stage 6 of `test.ps1` makes six single-token edits to `abi.cpp`, each removing exactly
one check the report depends on, rebuilds the native DLLs, and re-runs the suite. A
mutation that survives names a claim nobody is verifying.

## Decision

**Only `abi.cpp` is mutated.** Never `engine.cpp`.

Mutating the engine would demonstrate that the tests notice broken arithmetic, which
nobody doubted and which is not the claim under test. The claim is about the boundary, so
the mutations are in the boundary.

Each mutation must also *compile*. A mutation that does not build is not evidence about
the suite, so the harness treats a build failure as a harness error rather than a kill --
otherwise `if (false)` edits that trip `C4702` under `/WX` would be silently scored as
successes. That is why the mutations are `steps < 1` to `steps < 0` and
`o.volatility < 0.0` to `o.volatility < -1e308` rather than the more obvious deletions.

## What it found

Five of six mutations were killed on the first run. The survivor:

```cpp
if (out_capacity < count) {        →    if (out_capacity < 0) {
```

Deleting the native batch capacity check entirely -- the check whose absence is the
63-slot heap overwrite, the second of the project's three headline findings -- left the
entire suite green.

The suite *did* have a test for a short batch buffer. It asserted an `ArgumentException`
from `PricingEngine.PriceBatch`, which is thrown in managed code before the P/Invoke ever
happens. It was a test of the wrapper, not of the boundary, and it passes just as happily
when the boundary has no check at all.

This matters beyond the missing assertion. **Real callers of a C ABI do not go through
`PricingEngine`.** They are Python, Excel, another C++ program, a decade-old VB6 front
end, and whatever the next team writes. The managed wrapper is the cheapest place to stop
a bad call; it is not the place that protects anybody.

The fix is `The_native_boundary_refuses_a_short_buffer_and_writes_nothing_past_it`, which
calls `pj_price_batch` directly with a guard-filled over-allocation and asserts both
halves of the contract: status `Capacity`, and zero guard slots overwritten. Its
counterpart asserts the 2009 boundary returns `Ok` and overwrites all 63.

## Consequences

- Six mutations, six kills, enforced. A survivor fails the build.
- Every mutation is paired with a one-line description of *what claim it invalidates*,
  printed as it runs, so a survivor reports which sentence in `results.md` just became
  unverified rather than reporting "mutation 2 survived".
- The stage rebuilds native code six times and is slow by construction, at roughly
  thirty seconds per mutation. `build.ps1 -NativeOnly` exists to stop it also relinking
  managed assemblies no mutation touches.
- `finally { restore }` around every edit, and an unconditional rebuild afterwards, so an
  interrupted run cannot leave a mutant in the tree. `test.ps1` also verifies each
  mutation target string is present before editing, so a refactor that renames a variable
  fails loudly instead of silently mutating nothing and scoring six kills.

## The general lesson

The gap mutation testing found is the most common gap there is: **a test that asserts on
the outermost layer and believes it has tested the innermost one.** It is invisible to
coverage, because the line *is* covered -- by a call that never reaches it. It is
invisible to review, because the test is well named and does assert the right behaviour
at the level it operates. The only thing that finds it is deleting the check and seeing
whether anybody notices.
