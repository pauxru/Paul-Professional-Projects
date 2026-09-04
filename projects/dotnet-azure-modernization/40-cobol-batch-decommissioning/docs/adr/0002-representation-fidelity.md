# 0002 — Decoding preserves representation, not just value

**Status:** accepted

## Context

The obvious interface for a record reader is `bytes -> dict[str, Decimal | str]`.
It is what every example on the internet does, it is what a modern developer
expects, and it makes the data usable immediately.

It also loses information, and the information it loses is exactly the
information a decommissioning project needs.

COMP-3 packed decimal stores its sign in the low nibble of the last byte. Six
values are legal:

| nibble | meaning |
|---|---|
| `0xC` | positive (preferred) |
| `0xD` | negative (preferred) |
| `0xF` | unsigned (preferred for a PIC without `S`) |
| `0xA`, `0xE` | positive (alternate) |
| `0xB` | negative (alternate) |

So zero has at least three byte encodings — `0x0C`, `0x0D` and `0x0F` — and they
are all the number zero. `Decimal("0")` cannot tell you which one you read.

Zoned decimal has the same problem in the zone nibble of its signed byte, with a
twist: an encoder writing a positive value from scratch produces `0xC`, but real
files are full of `0xF` because a program `MOVE`d an unsigned value into a signed
field. And uninitialised working storage written to disk is EBCDIC `0x40`
repeated, which COBOL reads as zero.

In the 5,000-record corpus, 4,593 of 75,026 fields (6.1%) fall into one of these
categories, and they are spread across 3,026 records (60.5%).

## Decision

Decoding returns a `Cell` that carries, alongside the value:

- `sign_repr` — the actual sign nibble or zone that was read
- `space_positions` — which byte offsets inside the field held EBCDIC space
- `canonical` — whether re-encoding from the value alone reproduces the bytes

`encode_record(..., canonical_signs=False)` — the default — reproduces the
original representation exactly. `canonical_signs=True` deliberately does not; it
models what a naive reimplementation does, so the differ can measure the gap.

`canonical` is **computed, not inferred.** Every field is re-encoded both ways at
decode time and compared against the original bytes. A rule of thumb about
"preferred sign nibbles" would have been wrong for zoned positives, where `0xF`
is common in real data but `0xC` is what an encoder produces. Two extra encodes
per field cost about 360 µs per record, and buy a flag that cannot be wrong.

The representation-preserving path is also asserted on every decode: if
`encode(decode(x)) != x`, `decode_record` raises. That is a library bug, not a
data problem, and it should surface on the first record rather than in a diff
report at the end of a six-hour run.

## Consequences

The interface is heavier than `dict[str, Decimal]`. `Record.to_dict()` exists for
callers who genuinely only want values.

The payoff is that the harness can make a claim no value-level tool can make:
*this file is byte-identical to the one that went in, except in the field that
was supposed to change.* Section 7 of `docs/results.md` is that claim, measured.
