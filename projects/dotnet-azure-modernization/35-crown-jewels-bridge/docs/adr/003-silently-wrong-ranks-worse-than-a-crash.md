# ADR-003: silently wrong ranks worse than a crash

**Status:** accepted
**Date:** while designing the fuzz outcome taxonomy

## Context

Each fuzz case produces one of five outcomes. They are declared in this order, and the
order is load-bearing:

```csharp
public enum FuzzOutcome
{
    Accepted,          // returned a usable price
    RejectedCleanly,   // declined with a status code
    SilentlyWrong,     // reported success, returned something that is not a price
    MemoryCorruption,  // wrote past the caller's buffer
    ProcessDied,       // did not return
}
```

`FuzzSummary.UnsafeCases` counts the last three. The report sorts by the enum value. A
test asserts the ordering explicitly, because somebody tidying this list alphabetically
would silently invert the conclusion of the entire report.

## Decision

`SilentlyWrong` sorts **above** `MemoryCorruption` and only just below `ProcessDied`. A
wrong answer delivered with a success code is treated as a more severe defect than an
access violation.

## Why

This is counter-intuitive and it is the most important judgement in the project.

**A crash has already been detected.** The process died at 09:14. Somebody is looking at
it by 09:20. The blast radius is one process, the diagnosis has a stack trace, and the
bad data never reached anything downstream because there was no downstream.

**A wrong price with `PJ_OK` has not been detected and may never be.** It flows into the
position, the position flows into the risk report, the risk report flows into a limit
check that passes, and the trade settles. It is discovered weeks later in a
reconciliation, by someone with no reason to suspect the pricing library, and the
investigation starts from a difference of pennies across a book of thousands of trades.
The cost is not the wrong number. The cost is the search.

The measured shape of the 2009 boundary makes this concrete. Of 328 unsafe outcomes:

- 58 killed the process
- 7 corrupted memory
- **263 returned a wrong answer with a success code**

The failure mode that dominates is the one that is hardest to find. The crashes -- which
sound like the worst thing in the table -- are the part of the problem that would have
been fixed in 2009, because they announce themselves.

## The clearest single instance

`call(-sigma) == -put(sigma)`, exactly.

In the Black-Scholes d1, sigma appears squared in the numerator and once in the
denominator, so negating it maps `d1 -> -d1` and `d2 -> -d2`, and the call formula
becomes minus the put formula. This is an algebraic identity, verified in the suite to
twelve decimal places against the legacy DLL.

So a sign error anywhere upstream -- a feed handler, a spreadsheet, a units conversion,
a `-` typed where `+` was meant -- does not produce a NaN or an obviously silly number.
It produces a small, finite, well-behaved, plausible price for a *different contract*.
Nothing downstream can catch it. There is nothing wrong with the number. The only place
it can be caught is the boundary, by refusing an input that has no meaning.

## Consequences

- The hardened boundary checks *outputs* as well as inputs: `!std::isfinite(*out)`
  converts a NaN result into `PJ_ERR_NOT_CONVERGED`. Refusing to return a non-answer is
  as much a part of the contract as refusing to accept a non-input.
- The `SilentlyWrong` classifier in `FuzzRunner` is deliberately broad -- NaN, infinite,
  or negative all count. A negative option price is arbitrage; it is not a price.
- Relative magnitude is checked, not just finiteness. `strike = 1e300` with `spot = 42`
  passed every other test and returned exactly `0.0`: finite, non-negative, plausible,
  and corrupt. `kMaxMoneyness` exists for that case alone.
- The report's prose says it in one line, because a reader who skims the table needs the
  ranking to survive the skim: *"The crashes are the safe failures."*
