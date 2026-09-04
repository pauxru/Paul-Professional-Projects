# ADR-0002: A total order for trust, with lattice operations on top

**Status:** accepted

## Context

Provenance tracking needs an algebra. When text from two sources is
concatenated, the result has some trust level, and the rules for computing it
must be defined before any of the broker's guarantees mean anything.

The general answer is a lattice: trust levels form a partial order, and
combining is a meet (greatest lower bound). Real systems often need this,
because "came from the finance database" and "came from the user" are not
comparable — neither dominates the other.

## Decision

Trust is a **total** order — `SYSTEM > USER > TOOL > UNTRUSTED` — and the
combining operation is `min`, which is the meet of a chain.

The API is nonetheless written in lattice terms (`min_trust`,
`origins_at_or_below`, `slice_trust`), and the tests assert the lattice laws:
commutativity, associativity, idempotence, and that the meet is a lower bound
of both operands.

## Consequences

**The laws are checked, so the generalisation is available.** Replacing the
chain with a genuine partial order means changing the enum and the meet
implementation. Nothing else in the codebase depends on comparability, because
nothing outside `channels.py` compares two trust values directly.

**Concatenation takes the minimum, which is the only safe direction.** Joining
system text to untrusted text yields untrusted text. The alternative — that
the combination inherits the higher level — is precisely the confused-deputy
bug this whole system exists to prevent.

**Provenance is tracked per character, not per message.** `Tainted` carries a
span list, so `trust_of_substring` can answer "where did *this* argument come
from" rather than "how trusted was the message that produced it". Message-level
tracking would collapse the moment a prompt contains both a system instruction
and a retrieved document, which is every prompt.

**The top element is a hazard.** The meet of an empty set of spans is the
lattice's top — `SYSTEM` — which is vacuously correct and produced bug 6: the
empty substring was the most trusted input in the system. `trust_of_substring`
now returns `None` for an empty needle, distinguishing "no evidence" from
"evidence of high trust". That distinction is load-bearing and is the subject
of ADR-0003's `unattributable` rule.
