# ADR-001: `engine.cpp` is byte-identical in every variant, and never gets fixed

**Status:** accepted
**Date:** during initial design, reaffirmed twice under pressure

## Context

The project claims that a dangerous legacy C++ library can be made safe to call without
modifying it. That claim is only interesting if it is falsifiable, and it is only
falsifiable if the library genuinely does not change.

Three DLLs are built:

| DLL | `PJ_HARDENED` | floating point |
|---|---|---|
| `pricing.dll` | 1 | `/fp:precise` |
| `pricing_legacy.dll` | 0 | `/fp:precise` |
| `pricing_fast.dll` | 1 | `/fp:fast /arch:AVX2` |

All three compile the same `engine.cpp` from the same file on disk. The only difference
between the first two is a preprocessor definition, and every line it controls lives in
`abi.cpp`.

## Decision

`engine.cpp` is not modified for any reason, including to fix bugs the tests find in it.
Any behavioural difference between the hardened and legacy DLLs must be implementable
entirely within `abi.cpp`.

## Why this was hard to hold

Twice during the build, the obvious fix was in the engine.

**The degenerate-input case.** The hardened boundary was rejecting `volatility == 0` and
`years == 0`, which is unacceptable -- every option expiring today has `years == 0`, on
every expiry date. The natural fix is three lines in `black_scholes()` handling the
limit. It would have been correct, smaller, and in the right place by any ordinary
standard of software design.

It was rejected because it destroys the experiment. Once `engine.cpp` differs between
builds, "the danger is in the boundary, not the engine" stops being a measurement and
becomes an assertion, and every number in `results.md` becomes a comparison between two
things that differ in two ways. The fix went into `abi.cpp` instead, as
`degenerate_price()`, which computes the limit at the seam and never calls the core for
those inputs. It is slightly worse code in isolation and it is the only version that
preserves the finding.

**The negative-volatility identity.** `call(-sigma) == -put(sigma)` exactly. The engine
could clamp. Same reasoning, same answer: the hardened boundary refuses, the legacy
boundary happily returns a bookable price for the wrong instrument, and the contrast is
the result.

## Consequences

- `abi.cpp` carries logic that would ordinarily belong deeper. `degenerate_price()` is
  the clearest example: it is pricing code, in the boundary, on purpose.
- The hardened boundary ends up *more correct* than the engine on two classes of input,
  not merely stricter. That was not the plan, and it is the more interesting result: the
  boundary is where you can afford to be careful, because it is the part you are allowed
  to change.
- `test.ps1`'s mutation stage only ever mutates `abi.cpp`. Mutating the engine would
  demonstrate that the tests notice broken arithmetic, which nobody doubted.
- Stage 1 of `test.ps1` hashes all three DLLs and fails if any two are identical -- a
  broken variant target that emits one binary three times would otherwise make every
  comparison in the report a tautology reporting perfect agreement.

## What this maps to in a real engagement

This is the whole shape of the "we cannot touch it" conversation. The library has priced
the book correctly for fifteen years and the desk trusts it; the risk of a subtle change
is a real, quantified, business risk, and "the code would be cleaner" does not outweigh
it. The engineering move is not to win that argument. It is to make it unnecessary by
proving the dangerous surface is somewhere you *are* allowed to work.
