# 2. How it is built

Fifteen classes, about 1,900 lines of Java, no framework. The shape of the code is driven
entirely by one requirement: **nothing in the report may be something I believed rather than
something I measured.**

That requirement is more restrictive than it sounds, and it is what produced most of the design.

## Everything is probed, nothing is cited

`Engine.java` wraps H2 and SQLite behind one interface. `EngineTest` contains 23 tests, and
every one of them is a claim about driver behaviour that appears somewhere in the report:

| probe | H2 | SQLite |
|---|---|---|
| `'Äpfel'` vs `'äpfel'` under the engine's case-insensitive collation | folds them | **does not** -- `NOCASE` is A-Z only |
| `DECIMAL(18,4)` holding `10.0000` | `BigDecimal`, scale enforced | `Integer 10` |
| `INTEGER` holding 2^63-1 | **throws** -- H2's `INTEGER` is 32-bit | fine |
| `'0000007'` into the account column | kept (`VARCHAR(24)`) | coerced to `7` (`INTEGER`) |
| `CHAR(10)` holding `'OK'` | padded to 10 | length 2 |
| `TIMESTAMP` | `java.sql.Timestamp` | `Long` epoch millis |

I had guesses for most of these before running them and I was wrong about two. The
`Äpfel`/`äpfel` asymmetry is the one the entire collation section rests on, and I had assumed it
went the other way. H2 folds *more* than SQLite, so migrating in this direction *weakens* a
`UNIQUE(name)` constraint while leaving every row byte-identical -- which is exactly the kind of
defect no row-by-row verifier can see, and it only exists because the fold went the direction it
did rather than the direction I expected.

The H2 `INTEGER` overflow is a smaller story with a sharper edge. The taxonomy originally had
twelve hazards. `INTEGER_BOUNDARY` at 2^63-1 threw an exception on insert, which means it is not
a silent-corruption hazard at all -- it is a bug report. A hazard that throws is the easy case.
It came out of the taxonomy and section 10 says why.

`Engine.read()` uses `getObject`, not `getString`. This matters more than it looks: reading
everything as a string would hide half the divergences (`10.0000` and `10` both render as
`"10"` under some drivers) and invent the other half. `insertMap` uses `setObject` for the
mirror-image reason -- a typed setter would coerce the value on the way in and destroy the
hazard before the migration could exhibit it.

## Ground truth cannot come from the verifier

`Corpus.java` builds 29 rows, one per hazard instance, each carrying two labels:

- `Row.corrupting` -- whether the engine pair alone destroys information in this row under a
  *faithful* migrator. Two rows have it: the leading-zero account, and the amount too big for a
  double's mantissa.
- `Migrator.damages(Row)` -- whether a given defective migrator damages this row.

The second is derived from each migrator's own definition, not from running it and seeing what
the verifier says. `CorpusTest` asserts this by calling `damages()` on rows that were never
inserted into any database. If ground truth came from the verifier, every measurement in the
project would be a tautology.

`Migrator` provides one faithful control and five defective ones -- trimming, normalising,
rounding, numeric-account, uppercasing -- each a plausible thing a real ETL job does on purpose.

## Rules are objects that know their own properties

```java
public static final Rule IDENTIFIER = new Rule("identifier", Rule::asIdentifier, true, "account");
//                                                            injective ^        columns ^
```

Two design decisions live in that line, both earned by getting it wrong first.

**Column scoping** (ADR 003). Rules used to dispatch on the runtime type of the value.
`NUMERIC` matched anything implementing `Number`, which includes the target's epoch-millisecond
`Long`s, so `NUMERIC` consumed the timestamps before `TEMPORAL` saw them. Whenever `NUMERIC` was
enabled, `TEMPORAL` silently stopped working -- no exception, no failing test, just a verifier
reporting more differences, all of which looked like findings. I found it by noticing that
removing a rule sometimes *increased* the difference count, which should be impossible.

**`isInjective()` as a method, not a comment** (ADR 001). It is a property the tests interrogate.
`RuleTest` asserts that every rule claiming injectivity actually is one, over a corpus of value
pairs. And section 11 of the report cross-references the claim against the measured blindfold
matrix: no injective rule blinds the verifier anywhere. That cross-check is only possible
because the property is data.

## The report is a program

`Experiments.java` is the report. Eleven sections, each registering a prediction *before* the
measurement:

```java
r.expect("P9", "The full rule set will pass the gate: it has perfect precision on the "
             + "faithful migration, which is the configuration anyone would ship.");
// ... run the experiment ...
r.found("P9", false, "It fails, and it fails as NO_GO_BLIND. ...");
```

`Report.java` renders `expect`/`found` pairs as blockquotes and tallies them. Eleven predictions:
**three held, eight contradicted.** The contradicted ones are the reason the project is worth
reading, and the format makes it costly to quietly revise a prediction after seeing the result --
the prediction is in the source, above the code that tests it, in a diff.

`docs/results.md` is generated by `Main`, and `OperationsTest` re-runs `Experiments.run()` and
compares it byte-for-byte against the committed file. A stale report is a test failure, with the
line number of the first divergence.

## The bit that is not an experiment

`CutoverGate.java` is the one class meant to be lifted into a real project. It returns four
decisions:

- `GO` -- caught every applicable control, reported nothing
- `NO_GO_CORRUPTION` -- caught its controls and found real differences
- `NO_GO_BLIND` -- reported nothing, and could not catch a planted defect either
- `NO_GO_UNUSABLE` -- objected to more than 5% of rows, past which nobody triages the output

The order matters: blindness is checked before corruption, because a verifier that failed its
controls is not permitted to be believed when it does find something. And `NOISE_CEILING = 0.05`
is a judgement about human behaviour, not a measurement -- it is the least defensible number in
the project and `known-limitations.md` says so.
