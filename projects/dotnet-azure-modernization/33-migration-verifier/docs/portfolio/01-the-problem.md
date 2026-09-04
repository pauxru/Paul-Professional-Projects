# 1. The problem

A team migrates a customer table from one database to another. The migration script is
straightforward. It runs. Row counts match. Somebody runs a reconciliation query. It comes back
clean. The cutover proceeds.

Four months later a customer calls because their account number is wrong.

The migration was correct in every sense the team could check. What went wrong is not in the
script. It is in the gap between two engines' ideas of what a value is, and in the fact that the
tools used to look for the gap were built out of the same assumptions that created it.

## Why the obvious check does not work

Here is the first result the project produces. A faithful migration -- no bugs, no
transformation, a straight copy -- of 29 rows from H2 to SQLite. Row 1:

```
id       source 1                        (Integer)      target 1                     (Integer)
name     source customer-0               (String)       target customer-0            (String)
code     source OK                       (String)       target OK                    (String)
account  source 10001                    (String)       target 10001                 (Integer)
amount   source 10.0000                  (BigDecimal)   target 10                    (Integer)
active   source true                     (Boolean)      target 1                     (Integer)
seen     source 2024-01-15 09:00:00.0    (Timestamp)    target 1705309200000         (Long)
```

Three of seven columns differ, on every row, in a migration where nothing went wrong.

This is not an unusual pairing. It is what happens when a schema with `DECIMAL(18,4)`,
`BOOLEAN`, and `TIMESTAMP` meets a schema without them. The signal-to-noise ratio of a naive
comparison is not poor. It is zero, because the noise is total: a checksum comparison objects to
93% of the rows.

So nobody uses a naive comparison. What people build instead is a comparison with
canonicalisation rules -- normalise the decimal, coerce the boolean, parse the timestamp -- and
this is where the interesting problem starts.

## The trap

Every rule you add makes the verifier quieter. You add `numeric` and the amounts stop
complaining. You add `boolean` and the flags stop complaining. You add a rule to reconcile the
account column and it goes quiet too.

That last rule reconciled `'0000007'` with `7`.

The account column was `VARCHAR(24)` in the legacy schema, holding account numbers with leading
zeros, and somebody sensibly tightened it to `INTEGER` in the new one. The leading zeros are
gone and cannot be recovered -- `'7'`, `'07'`, and `'0000007'` are now the same account. That is
the defect the migration introduced, and the rule you added to reduce noise is the rule that
hides it.

**The verifier cannot tell you this, because a rule that suppresses noise and a rule that
suppresses evidence produce identical output: silence.**

This is not a hypothetical. Section 6 of the report measures it: with the full rule set, every
defective migrator produces zero false positives and non-zero false negatives. The verifier is
maximally quiet and substantially blind, at the same time, for the same reason.

## Why nobody notices

There is a second effect, measured in section 3, that explains why teams give up on this rather
than solving it.

Seven rules produce 128 possible rule sets. Of those, **120 false-flag all 27 clean rows, and 8
flag none. Nothing lands in between.** The 8 that work are exactly the subsets containing all of
`{numeric, boolean, temporal, identifier}`.

The consequence is that verifier tuning has no gradient. You are missing four necessary rules;
you correctly add the first one; the output does not change. You add the second; the output does
not change. The third; nothing. There is no feedback until you happen to have all four, and
there is no indication which four. Every intermediate state of a correct debugging process is
byte-identical to the broken starting state.

At that point the reconciliation gets a `WHERE` clause excluding the noisy columns, or a
tolerance, or a note in the runbook saying the differences are expected. All three are ways of
turning the verifier off while leaving it running.

## What is actually needed

Two things, and the project is an argument that both are obtainable.

**A criterion for which rules are safe**, that does not depend on knowing which defects are
present -- because if you knew that, you would not need the verifier. That is the injectivity
result: a rule that never maps two distinct inputs to the same output cannot make a defect look
like agreement. It is checkable by inspecting the rule alone.

**A way to make silence mean something.** "The verifier reported no differences" is consistent
with a correct migration and with a verifier that cannot detect anything. Both produce the same
clean report, and they are the two most different situations you can be in. The cutover gate
plants known defects and requires the verifier to be observed catching them before its silence
is admitted as evidence.

The uncomfortable finding is what happens when you apply the second to the configuration the
first would have you ship anyway. Section 9: the perfect-precision verifier fails its own
controls. Both mechanisms are necessary and neither is sufficient.
