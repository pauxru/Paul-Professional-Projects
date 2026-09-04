# ADR-002: a fuzz run without a control group is not evidence

**Status:** accepted
**Date:** after the first fuzz run produced a perfect score

## Context

The first version of the fuzz experiment generated 600 hostile inputs and ran them
through both boundaries. The hardened boundary scored:

| outcome | count |
|---|---:|
| refused with a status code | 600 |
| silently wrong | 0 |
| memory corruption | 0 |
| process died | 0 |

A clean sheet. It went into the report as the headline.

It is worthless, and it took an uncomfortably long time to see why.

## The problem

**A boundary that refuses every input scores exactly the same.** So does a boundary
whose first line is `return PJ_ERR_BAD_ARG;`. The corpus could not distinguish safety
from total paralysis, which means it was not measuring safety at all -- it was measuring
"does this function ever return `Ok`", and answering "no".

That is not a hypothetical failure mode. It is the *likely* one. Under pressure to make
a fuzz report look good, the cheapest move available to any engineer is to tighten
validation until nothing gets through, and the metric rewards it. A metric that rewards
breaking the system is worse than no metric, because it comes with a number attached.

## Decision

One case in four is a deliberately legal option: every field in range, lattice steps
sane, batch buffer large enough. Those cases carry two obligations the hostile ones
cannot:

1. The hardened boundary must **accept** them and return a usable price.
2. The price must be **bit-identical** to the price the 2009 boundary returns for the
   same input.

`FuzzSummary.ControlHeld` is false if any control case was refused, and the report is
required to state the result. `test.ps1` reads it back out of the generated file with a
regex backreference -- `accepted (\d+) of \1` -- so "accepted 149 of 150" fails the
build and no threshold has to be maintained by hand.

## Why the second obligation matters as much as the first

Acceptance alone proves the boundary is not paralysed. It does not prove the boundary
left the answers alone. The most embarrassing possible outcome of this project is a
provably safe boundary that quietly reprices the book by a basis point, and nothing in
the hostile corpus would ever detect it, because hostile inputs have no correct answer to
compare against.

The tolerance is exact equality, not a small epsilon. Both DLLs compile the same
`engine.cpp` with the same flags and execute the same instructions, so anything less than
bit-identical would mean the preprocessor definitions had leaked into the arithmetic --
which is precisely the failure ADR-001 exists to prevent, detected from the other side.

## Consequences

- The hostile sample dropped from 600 to 450, and the headline unsafe count for the 2009
  boundary dropped correspondingly. A smaller true number is worth more than a larger
  meaningless one.
- The report now says "the hardened DLL accepted 150 of 150, so the zeroes above are
  safety rather than paralysis" in prose, immediately under the table. A reader who takes
  only the table away has still been told.
- Two tests exist rather than one: `The_hardened_boundary_survives_the_entire_corpus`
  and `And_the_clean_sheet_means_something_because_the_control_group_held`. They are
  deliberately separate and deliberately named that way, because reading the first
  without the second is the exact mistake this ADR is about.
- The control group also has to be *checked* -- `Every_control_case_is_genuinely_legal`
  asserts the generator never emits a control the boundary would be right to refuse.
  Without it, a generator bug becomes a false positive, and the natural response to a
  false positive is to loosen the boundary, which would make the system less safe by way
  of a broken test.

## The general form

Any experiment whose success condition is "nothing bad happened" needs a control that
fails when nothing at all happened. This applies well beyond fuzzing: a WAF that blocks
all traffic, a validator that rejects every payload, a circuit breaker permanently open,
a test suite where every test is skipped. All of them report perfect safety.
