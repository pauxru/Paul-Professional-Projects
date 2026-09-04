# Decommissioning a COBOL batch job without changing the file

A nightly job called `BILLRUN` has read the customer master file, applied a
service charge and VAT, and written the file back every night since 1994. It is
two hundred lines of COBOL and it is the only place the charging rules are
written down. The business wants it in Python. Nobody wants to be the person who
changed the file.

This is the harness that makes that safe: a copybook-driven reader that can
prove, byte for byte, that a reimplementation produced the same file — and an
experiment showing exactly which plausible reimplementations don't, and which of
those a normal test plan will fail to catch.

Everything here is Python 3.12 with no third-party dependencies.

```powershell
.\test.ps1          # 61 tests
.\demo.ps1          # the full argument, end to end
.\demo.ps1 -Save    # regenerate docs/results.md
```

## The headline

Over a 5,000-record corpus (all figures from [`docs/results.md`](docs/results.md),
reproducible from seed `20240612`):

| | |
|---|---|
| Records rewritten by a *correct* reimplementation | **3,026 of 5,000 (60.5%)** |
| Numeric values changed by those rewrites | **0** |
| Those rewrites visible as a **value** change to the downstream archive job | **506** |
| Records where the "obviously correct" rounding differs from the mainframe | **2,599 (52.0%)** |
| Records where the *dangerous* rounding differs | **1,607 (32.1%)** |
| ...of which appear in a test extract taken from a quiet week | **0** |

Two findings, and the second is the one worth the money.

### 1. A reimplementation can be correct and still rewrite 60% of the file

Decode every field, re-encode every field, change nothing. The result differs
from the input on 3,026 of 5,000 records, and not one number is different.

COMP-3 packed decimal stores its sign in a nibble, and **six** nibble values are
legal: `0xC` and `0xF` positive, `0xD` negative, and `0xA`, `0xE`, `0xB` as
alternates that real compilers and real hand-written assembler emit. Zoned
decimal has the same problem in its zone nibble, where `0xF` for a positive value
is overwhelmingly common in files written by programs that `MOVE`d an unsigned
value into a signed field — but an encoder working from a value alone produces
`0xC`. And uninitialised working storage written to disk is EBCDIC `0x40`, which
COBOL reads as zero and any modern encoder writes back as `0xF0`.

All of those decode to the right number. None of them survive a round trip
through a representation that stores only the number. In this corpus, 4,593 of
75,026 fields (6.1%) cannot be reproduced from their value alone.

The fix is not clever, it is just a decision made early: **decoding returns how a
value was written, not only what it was.** Every `Cell` carries its sign nibble,
its zone, and the byte positions that held spaces, and `encode_record` reproduces
them. See [ADR 0002](docs/adr/0002-representation-fidelity.md).

### 2. The representation change becomes a *value* change downstream

`CM-KEY-ALT REDEFINES CM-KEY PIC X(12)` — the archive job reads the account key
as text, because it has its own copybook and always has.

When a blank-padded `CM-ACCOUNT` is rewritten in canonical form, EBCDIC spaces
become EBCDIC zeroes. The number is unchanged. The text is not:

```
record 0: '    03112574' -> '000003112574'
```

506 records. A field-by-field comparison against the copybook reports success on
all of them, because through *our* copybook nothing changed. The archive job
starts seeing keys it has never seen before, three weeks after cutover, and by
then the old system is gone.

This is why the differ reports **byte equivalence and field equivalence
separately** rather than picking one. See [ADR 0004](docs/adr/0004-two-kinds-of-equivalence.md).

### 3. The arithmetic bug that survives testing

`CM-BALANCE` is `PIC S9(7)V99`. COBOL stores a computed result by **truncating
toward zero**; there is no `ROUNDED` in this program. Three plausible modern
implementations:

| implementation | differs from mainframe | on positive balances | on negative balances |
|---|---|---|---|
| `ROUND_HALF_UP` — "the correct one" | 2,599 (52.0%) | 1,753 | 846 |
| `ROUND_FLOOR` — `int()`, `//`, `math.floor` | 1,607 (32.1%) | **0** | 1,607 |
| `float` | 2,519 (50.4%) | 1,691 | 828 |

`ROUND_HALF_UP` is wrong half the time and is caught on day one.

`ROUND_FLOOR` is identical to the mainframe on **every positive amount** and
wrong on **every negative one**. Run it against a test extract with no refunds
and no credit balances — 3,347 records, the kind of extract a tester actually
pulls — and it differs on **zero** of them. It ships. It is discovered on the
first refund run.

The ordering matters: the implementations are ranked by how often they fail, and
inversely ranked by how likely they are to reach production.

### 4. Size errors nobody declared

722 of 5,000 records produce a balance too large for `PIC S9(7)V99`. There is no
`ON SIZE ERROR` clause, so the mainframe silently drops the high-order digits —
10,766,540.84 is stored as 766,540.84. This harness reproduces that on purpose
and counts it, because the alternative is a reimplementation that raises an
exception at 03:00 on a night nobody is watching. Neither behaviour is obviously
right; the point is that the question gets asked before cutover.

## What's here

| | |
|---|---|
| `cobol/ebcdic.py` | Explicit CP037/CP500 tables. The code page is a required argument, never a default. |
| `cobol/picture.py` | `PICTURE` clause parsing — the subset that appears in interchange records. |
| `cobol/numeric.py` | COMP-3, zoned decimal, COMP. Decoders return representation as well as value. |
| `cobol/copybook.py` | Copybook → layout: levels, `OCCURS`, `OCCURS DEPENDING ON`, `REDEFINES`, column-7 comments. |
| `cobol/record.py` | Decode and encode whole records, with an exact self-check on every field. |
| `cobol/generate.py` | Deterministic corpora containing the awkward-but-legal cases. Built independently of the encoder. |
| `cobol/differ.py` | Byte-level and field-level equivalence, reported separately. |
| `cobol/pipeline.py` | The business logic being decommissioned, in four arithmetics. |

## Design decisions worth arguing about

- [0001 — the copybook is the specification](docs/adr/0001-copybook-is-the-spec.md)
- [0002 — decoding preserves representation, not just value](docs/adr/0002-representation-fidelity.md)
- [0003 — the generator does not use the encoder](docs/adr/0003-independent-generator.md)
- [0004 — two kinds of equivalence, reported separately](docs/adr/0004-two-kinds-of-equivalence.md)
- [0005 — modelling mainframe arithmetic, including its bugs](docs/adr/0005-mainframe-arithmetic.md)

[What this does not do](docs/known-limitations.md) — and it is a longer list than
the feature list, deliberately.

## The story

- [The problem](docs/portfolio/01-the-problem.md)
- [What a clean test file proves](docs/portfolio/02-what-a-clean-test-file-proves.md)
- [The bug that passes testing](docs/portfolio/03-the-bug-that-passes-testing.md)
- [How I'd phase the real migration](docs/portfolio/04-phasing-the-migration.md)
