# 0003 — The generator does not use the encoder

**Status:** accepted

## Context

The strongest test in this project is a round trip: generate a corpus, decode
every record, re-encode it, assert the bytes are identical.

That test is worthless if the generator calls `encode_record`. It would prove
that `encode_record` is a left inverse of `decode_record` — that the two agree
with each other — and nothing at all about whether either agrees with a
mainframe. A shared misunderstanding of, say, how many bytes a nine-digit COMP-3
field occupies would be invisible: both sides would use the wrong number
consistently and the test would pass.

## Decision

`cobol/generate.py` lays out bytes itself. It walks the `Field` tree, computes
its own cursor, and calls the low-level `numeric.encode_*` primitives directly.
It never calls `encode_record`, and it never asks `Record` for anything.

It then asserts its own output length against `record_length()` — computed by the
copybook layout engine, a separate code path — and raises if they disagree:

```
generator produced 79 bytes, the layout engine says 80;
one of them is wrong and it is not safe to guess which
```

The round trip is therefore between two implementations that share only the
copybook and the format definition.

## The second half of the decision: generate dirty data

A generator that emits well-formed canonical records proves the decoder handles
the case nobody doubted. `Corruption` controls the rates at which the generator
emits things that are legal, present in real files, and hostile to a naive
reader:

- non-preferred COMP-3 sign nibbles `0xA`, `0xB`, `0xE` (12% of signed fields)
- zoned positives written with zone `0xF` rather than `0xC`
- fields blank-padded with EBCDIC `0x40` from uninitialised working storage (5%)
- negative zero (4% of signed fields)
- variable-length records via `OCCURS DEPENDING ON`

`Corruption.none()` turns all of that off, and section 8 of `docs/results.md`
runs the same naive re-emission against a clean corpus: **byte-identical, 1,000
of 1,000 records.**

That contrast is the point of the whole module. The clean corpus is the test file
a migration team writes for itself. It passes. Then the real file arrives.

## Consequences

There is duplicated layout logic — `generate.py` and `record.py` both walk the
tree and both handle `OCCURS DEPENDING ON`. That duplication is load-bearing and
should not be refactored away; a comment in `generate.py` says so.

The corruption rates are chosen, not measured from a real file, so the absolute
percentages in the results are a property of the generator. What is *not* a
property of the generator is the shape of the result: at any non-zero rate, naive
re-emission changes bytes and no value-level test notices.
