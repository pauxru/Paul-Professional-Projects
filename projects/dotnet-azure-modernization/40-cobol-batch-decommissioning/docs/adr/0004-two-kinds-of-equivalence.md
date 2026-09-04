# 0004 — Two kinds of equivalence, reported separately

**Status:** accepted

## Context

"Is the new output the same as the old output?" has two honest answers.

**Field equivalence** — every field decodes to the same value. This is what the
business means by "the same", and it is what a reimplementation is normally
tested against.

**Byte equivalence** — the files are identical. This is what the *downstream*
systems mean by "the same", because several of them do not use your copybook.
They index at hard-coded offsets, or take a checksum, or were written in 1998 by
someone who assumed the sign nibble was always `0xC`.

A differ that picks one of these and reports a boolean will be wrong for half its
audience.

## Decision

`DiffReport` reports both, and separates the differences into two buckets:

- `value_diffs` — the field decodes to a different value
- `representation_diffs` — the field decodes to the *same* value, written
  differently

and exposes `field_equivalent` and `byte_equivalent` as independent properties.
The verdict line is deliberately three-valued:

```
verdict : field-equivalent but NOT byte-equivalent
```

That sentence is the one worth being able to say out loud in a go/no-go meeting.

## Why this turned out to matter more than expected

The corpus contains `CM-KEY-ALT REDEFINES CM-KEY PIC X(12)` — the archive job
reads the account key as text. When a blank-padded `CM-ACCOUNT` is rewritten in
canonical form, EBCDIC spaces become EBCDIC zeroes. Through the numeric copybook
entry, nothing changed. Through the overlay, the key changed from `'    03112574'`
to `'000003112574'`, on 506 of 5,000 records.

So the neat two-category model is itself slightly wrong: **a representation
change is a value change to anyone holding a different copybook.** The differ
surfaces this because it decodes `REDEFINES` overlays and reports them like any
other field, even though it never uses them to write bytes back.

That was not the plan. It fell out of deciding to decode overlays at all, which
was originally just for completeness.

## Other decisions inside the differ

**Records are walked with the copybook, not sliced at a fixed length.** With
`OCCURS DEPENDING ON` there is no fixed length.

**A length disagreement stops the comparison.** Once two files disagree about
where a record ends, every subsequent record is misaligned and the diff becomes
thousands of lines of noise. Reporting the first mismatch and stopping is more
useful than reporting all of them.

**A decode failure is a result, not an exception.** Truncated files exist. The
differ catches `RecordError`, records it in `decode_error`, and returns a report
rather than propagating.

**`by_field()` collapses array subscripts,** so `CM-TXN-TABLE[0].CM-TXN-AMOUNT`
and `CM-TXN-TABLE[3].CM-TXN-AMOUNT` aggregate. Otherwise a five-element table
produces five rows saying the same thing.

## Consequences

`max_diffs` defaults to 200 and sets `truncated`, which forces both equivalence
properties to `False`. A truncated report can prove a file *differs*; it can
never prove one matches. Callers that need a verdict pass a large `max_diffs` —
the demo passes `10**9`.
