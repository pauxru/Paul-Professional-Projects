# Known limitations

Written to be read by someone deciding whether to trust the conclusions.

## 1. Two engines, not a survey

Every measurement is H2 -> SQLite. The mechanisms are general -- type affinity, collation
folding, decimal-to-binary conversion, fixed-width padding -- but the *specific* numbers are not
transferable. Oracle -> PostgreSQL would produce a different corpus, a different rule set, and
almost certainly a different lattice shape. What transfers is the method: probe the engine pair,
label the corpus by ground truth, plant controls, measure the rules.

The one thing I would expect to reproduce anywhere is the injectivity criterion, because it does
not depend on the engines at all.

## 2. The converse of the injectivity result is unproven

Measured: no injective rule blinded the verifier in any configuration. **Not** measured: that
every non-injective rule blinds it. Two of the four non-injective rules (`trim`, `temporal`)
never blinded anything here. That is a fact about this corpus -- it means the corpus has no row
where trailing whitespace is the only carrier of a planted defect -- and not evidence that those
rules are safe. Absence from the blindfold matrix is weak evidence.

So the criterion is used in one direction only: injective implies safe. A non-injective rule
requires a specific argument about the specific column, which is what the ADRs contain.

## 3. Cross-machine timestamp non-determinism is argued, not demonstrated

Section 8 shows that the target stores `2024-03-31 02:30:00` as epoch milliseconds with no zone,
and that reading that number back through four `ZoneId`s yields four different wall clocks. That
much is measured.

What I wanted to show was the stronger claim: that the *same migration code* run on two machines
in different zones writes two different numbers. I could not demonstrate it in-process.
`TimeZone.setDefault()` after the JDBC driver has been loaded does not reach the driver's
conversion path -- prediction P7, contradicted, and left in the report as an honest negative
rather than quietly deleted.

Demonstrating it properly needs two JVMs started with different `-Duser.timezone` values and
their outputs compared. The build does pin the surefire JVM to UTC for exactly this reason, so
the machinery is half-built; the experiment is not.

## 4. Twenty-nine rows

The corpus is small enough to reason about by hand, which is the point -- every row is labelled
with what it is for. It is far too small for the statistics to mean anything as statistics. A
precision of 0.07 on the dual-write path means "3 of 43", not "7%" in any population sense.

The corpus is also *adversarial by construction*: every row is there because it is hard. Real
tables are mostly boring rows, so a real precision figure would be far higher and far less
informative. This is deliberate -- section 10 shows the opposite failure, where
`DECIMAL_TO_BINARY_FLOAT` destroys `99999999999999.1234` and leaves `0.10` perfectly
recoverable. A corpus of plausible small values would have concluded the hazard was harmless.
That is precisely how migration test suites fail to predict production.

## 5. The `temporal` rule contains a heuristic I do not like

`Rule.TEMPORAL` treats any `Long` above 1e12 in the `seen` column as epoch milliseconds. A
genuine identifier of that magnitude in that column would be silently reinterpreted. It is
column-scoped, so the blast radius is one column, and a test asserts small longs are left alone
-- but it is a heuristic, and the report's own thesis is that heuristics inside verifiers are
where the blindness comes from. I kept it because removing it makes the verifier unusable (93%
of rows flagged) and because naming the problem is better than hiding it.

## 6. The gate's noise ceiling is a judgement, not a measurement

`NOISE_CEILING = 0.05`. I did not derive that from anything. It encodes a belief about human
behaviour -- that past roughly one row in twenty, nobody triages the output and somebody raises
the threshold instead. It is the least defensible constant in the project. It is also the reason
`naive-checksum`, which catches 5 of 5 planted controls and is the most *sensitive* verifier
measured, is rejected as unusable.

## 7. No concurrency

`DualWrite` and `Backfill` model the ordering hazards of a live cutover deterministically, with
an explicit interleaving, on one thread. Real cutovers add lock contention, replica lag, and
partial failure. The staleness window this project measures is real and its dependence on batch
size is measured; it is a lower bound on the trouble available.

## 8. Ground truth is declared, not discovered

`Migrator.damages(Row)` is derived from each migrator's own definition, so it cannot circularly
agree with the verifier -- there is a test asserting it works on rows that were never inserted
into any database. But it is still *my* declaration of what constitutes damage. The distinction
between "corruption" and "representation" is defended in ADR 001 rather than proven, and the
whole project rests on it.
