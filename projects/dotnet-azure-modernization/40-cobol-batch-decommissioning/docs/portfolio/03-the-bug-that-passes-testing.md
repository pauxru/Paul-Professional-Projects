# The bug that passes testing

I built the four arithmetic implementations expecting to produce a boring result:
*use `Decimal`, don't use `float`*. Everyone knows that. It would have been a
paragraph in the README.

The actual result inverted the thing I thought I was measuring.

## The setup

`CM-BALANCE` is `PIC S9(7)V99`. COBOL stores a computed value into that field by
**truncating toward zero** — there is no `ROUNDED` in this program. Three
plausible modern implementations of the same rule:

| implementation | how you get it |
|---|---|
| `ROUND_HALF_UP` | `Decimal.quantize(Decimal("0.01"))` with the obvious rounding argument |
| `ROUND_FLOOR` | `int()`, `//`, or `math.floor` on a scaled integer |
| `float` | using `float` for money |

Over 5,000 records:

| implementation | differs | on positive balances | on negative balances |
|---|---|---|---|
| `ROUND_HALF_UP` | 2,599 (52.0%) | 1,753 | 846 |
| `ROUND_FLOOR` | 1,607 (32.1%) | **0** | 1,607 |
| `float` | 2,519 (50.4%) | 1,691 | 828 |

## Read the `ROUND_FLOOR` row again

Zero differences on positive balances. Every single difference is on a negative
one.

That is not a coincidence and it is not noise. Floor and truncate-toward-zero are
*the same operation* for non-negative numbers. They diverge only below zero,
where floor goes away from zero and truncation goes toward it. `-17.9375`
truncates to `-17.93` and floors to `-17.94`.

So `ROUND_FLOOR` is a bug that is perfectly correlated with the sign of the
input.

## Which means the test extract passes

I simulated what a tester actually does: pull a slice of the master file and run
the old and new implementations against it. The slice in section 5 of
[`docs/results.md`](../results.md) is 3,347 records with a non-negative balance —
a quiet week, no refunds, no credit balances.

```
mode         differs     rate
-----------------------------
half-up         1753  52.38%
floor              0   0.00%
float           1691  50.52%
```

`ROUND_HALF_UP` — the implementation any reviewer would call correct, the one
that follows the rounding rule people learn at school — fails on more than half
the extract. It is caught within an hour and never reaches a release branch.

`ROUND_FLOOR` passes cleanly. Every record. It goes to production. It is wrong on
a third of the real file, by a penny each time, in one direction, and it stays
invisible until the first refund run.

## The inversion

The three implementations are ordered by how often they fail:

```
half-up (52.0%)  >  float (50.4%)  >  floor (32.1%)
```

and they are inversely ordered by how likely they are to survive testing. The
implementation that fails most often is the safest one to write, because it fails
loudly. The implementation that fails least often is the dangerous one, because
its failures are *correlated with a data characteristic your sample lacks*.

That generalises, and it is the sentence I actually took away from this project:

> A defect's danger is not its frequency. It is the correlation between the
> defect and the sampling bias of your test data.

Uniform-random failures get caught. Failures that cluster on a rare-in-sample,
common-in-production characteristic do not. Negative amounts, leap days,
non-ASCII names, records created before a schema change, customers in one
timezone — these are the shapes to hunt for, and the way to hunt for them is to
ask what your extract *cannot* contain.

## What I changed because of it

The `compare_modes` function reports `differing_positive` and
`differing_negative` separately rather than a single rate. A single rate would
have shown `floor` as the *best* of the three wrong implementations, which is
exactly backwards, and I would have written it up that way.

There is a test asserting the shape rather than the number:

```python
self.assertEqual(floor["differing_positive"], 0)
self.assertGreater(floor["differing_negative"], 0)
```

That test would fail if someone "fixed" the reference implementation to round,
which is the change most likely to be proposed by a reviewer who has not read
[ADR 0005](../adr/0005-mainframe-arithmetic.md).

## The one I didn't expect at all

While measuring this, the differ started reporting *value* differences on
`CM-KEY-ALT` during the pure re-encode run — the run where, by construction,
nothing should change value.

`CM-KEY-ALT REDEFINES CM-KEY PIC X(12)`. The archive job reads the account key as
text. A blank-padded `CM-ACCOUNT` rewritten in canonical form turns EBCDIC spaces
into EBCDIC zeroes:

```
'    03112574'  ->  '000003112574'
```

506 records. Through our copybook, nothing changed — the number is identical.
Through *their* copybook, every one of those keys is new.

I had built the differ around a clean two-category model: value differences and
representation differences. That model is wrong, and the file itself said so.
**A representation change is a value change to anyone holding a different
copybook.** It only surfaced because I decided to decode `REDEFINES` overlays for
completeness, and then failed a test I had written to assert `value_diffs == []`.

The right move was to keep the failing test's *finding* and delete its
assumption. That test is now
`test_representation_change_becomes_a_value_change_through_a_redefines`, and it
is the most valuable test in the repository.

Next: [how I'd phase the real migration](04-phasing-the-migration.md).
