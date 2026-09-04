# Verifying a heterogeneous database migration

Two engines stand in for the source and the target: H2 with `SET IGNORECASE TRUE`, and SQLite with `COLLATE NOCASE` on the same column. The migration ticket calls these equivalent. They are not, and most of what follows is a consequence of that one sentence in the ticket being false in a way nobody checked.

Every hazard in the taxonomy was measured against the real drivers before it was written down. Two were measured and dismissed. One announced itself by throwing an exception, which disqualified it: a hazard that throws is a bug report, and will be fixed on the day it appears. This project is about the quiet ones.

The corpus is 29 rows. That is small on purpose -- every result here is a statement about *which* defects a verifier can see, not about how many rows it can process, and a bigger corpus would add running time without adding a single new claim.


## 1. What the two engines actually disagree about

Under a migrator that copies every value through untouched, these are the columns on which the target's stored value differs from the source's, as the JDBC drivers hand them over. `getObject`, not `getString`: a rendering would hide half of this and invent the other half.

| column | rows differing |  |
|---|---|---|
| `name` | 0 | identical |
| `code` | 0 | identical |
| `account` | 29 | differs |
| `amount` | 29 | differs |
| `active` | 29 | differs |
| `seen` | 29 | differs |

Row 1, side by side, with the Java type the driver returned:

```
id       source 1                        (Integer)        target 1                        (Integer)
name     source customer-0               (String)         target customer-0               (String)
code     source OK                       (String)         target OK                       (String)
account  source 10001                    (String)         target 10001                    (Integer)
amount   source 10.0000                  (BigDecimal)     target 10                       (Integer)
active   source true                     (Boolean)        target 1                        (Integer)
seen     source 2024-01-15 09:00:00.0    (Timestamp)      target 1705309200000            (Long)
```

Three columns differ on every single row of a migration in which nothing went wrong. That is the entire difficulty of the problem in one table: the signal-to-noise ratio of a naive comparison is not poor, it is zero, because the noise is total.


## 2. The four verifiers

Four strategies, run against a faithful migration. Ground truth is that two rows are genuinely corrupted -- an account number that lost its leading zeros to type affinity, and an amount too large for a double's mantissa. Both are unrecoverable. Everything else is representation.

> **Predicted (P1)** -- The row-count check will catch nothing, and the naive checksum will do better than it by catching some things and missing others.

| verifier | true pos | false pos | false neg | precision | recall |
|---|---|---|---|---|---|
| `row-count` | 0 | 0 | 2 | 1.00 | 0.00 |
| `naive-checksum` | 2 | 27 | 0 | 0.07 | 1.00 |
| `canonicalising[(none)]` | 2 | 27 | 0 | 0.07 | 1.00 |
| `canonicalising[trim+nfc+numeric+boolean+casefold+temporal+identifier]` | 2 | 0 | 0 | 1.00 | 1.00 |

> **Contradicted (P1)** -- Half right, and wrong about the interesting half. The row count does catch nothing (recall 0.00). But the naive checksum misses nothing at all -- recall 1.00 -- and flags 27 of 29 rows that are fine. Its failure mode is not blindness, it is the opposite. It is so loud that it will be switched off, and on the day it is switched off it takes the two real findings with it. A verifier is not removed for missing things. It is removed for wasting people's afternoons.


## 3. The rule lattice: tuning has no gradient

There are 7 canonicalisation rules, so 128 possible rule sets. Each was run against the faithful migration and scored by how many clean rows it false-flagged.

> **Predicted (P2)** -- False positives will fall off gradually as rules are added, so an engineer tuning the verifier gets steady feedback and can stop when it is quiet enough.

| false positives | how many of the 128 rule sets |
|---|---|
| 0 | 8 |
| 27 | 120 |

> **Contradicted (P2)** -- There is no gradient at all. 120 of the 128 rule sets false-flag all 27 clean rows and 8 flag none; nothing lands in between. The 8 that work are exactly the sets containing all of [numeric, boolean, temporal, identifier], with the remaining 3 rules ([trim, nfc, casefold]) free to be on or off.

This is the mechanism by which verifier tuning is abandoned. An engineer adds the `trim` rule, because CHAR padding is the most obvious difference; nothing improves. Adds `nfc`; nothing improves. Adds `casefold`; nothing improves. Three correct changes, each of which fixed a real class of false positive, and the output is byte-identical every time. The reasonable conclusion after the third attempt is that comparing this data is hopeless, and the reasonable next step is a row count.

The improvement only appears when the last of `numeric`, `boolean` and `temporal` goes in, because a row is flagged if *any* column differs and each of those three differs on every row. Marginal value is zero until the set is complete, and then it is everything. Nobody gets that kind of feedback from an incremental process and keeps going.


## 4. The blindfold matrix

Every canonicalisation rule was added to suppress a real, harmless difference, and every one of them suppresses a harmful difference that looks the same. Here is what each one costs.

Each defective migrator below is one I have seen in production. None of them was written carelessly; each has a one-line justification that is true.

| migrator | why it was written that way |
|---|---|
| `trimming` | Strips whitespace so CHAR padding stops producing diffs. |
| `normalising` | NFC-normalises text so accented names are searchable. |
| `rounding` | Rounds money to 2dp so reports render in pence. |
| `numeric-account` | Parses account as a long to match the target's INTEGER column. |
| `uppercasing` | Upper-cases names for consistent case-insensitive lookup. |

> **Predicted (P3)** -- Each rule will blind the verifier to the one migrator it corresponds to, and removing the rule will restore detection without other effects.

| defect | rule removed | true pos | false pos | what the removal bought |
|---|---|---|---|---|
| `trimming` | `trim` | 2 -> 3 | 0 -> 26 | recall bought with 26 false positives |
| `trimming` | `numeric` | 2 -> 3 | 0 -> 26 | recall bought with 26 false positives |
| `trimming` | `boolean` | 2 -> 3 | 0 -> 26 | recall bought with 26 false positives |
| `trimming` | `temporal` | 2 -> 3 | 0 -> 26 | recall bought with 26 false positives |
| `trimming` | `identifier` | 2 -> 3 | 0 -> 26 | recall bought with 26 false positives |
| `normalising` | `nfc` | 2 -> 3 | 0 -> 0 | **pure blindfold** -- recall for free |
| `normalising` | `numeric` | 2 -> 3 | 0 -> 26 | recall bought with 26 false positives |
| `normalising` | `boolean` | 2 -> 3 | 0 -> 26 | recall bought with 26 false positives |
| `normalising` | `temporal` | 2 -> 3 | 0 -> 26 | recall bought with 26 false positives |
| `normalising` | `identifier` | 2 -> 3 | 0 -> 26 | recall bought with 26 false positives |
| `uppercasing` | `numeric` | 2 -> 26 | 0 -> 3 | recall bought with 3 false positives |
| `uppercasing` | `boolean` | 2 -> 26 | 0 -> 3 | recall bought with 3 false positives |
| `uppercasing` | `casefold` | 2 -> 26 | 0 -> 0 | **pure blindfold** -- recall for free |
| `uppercasing` | `temporal` | 2 -> 26 | 0 -> 3 | recall bought with 3 false positives |
| `uppercasing` | `identifier` | 2 -> 26 | 0 -> 3 | recall bought with 3 false positives |

> **Contradicted (P3)** -- The correspondence is real but the clean cases are the exception. Only 2 rule/defect pairs are pure blindfolds -- {nfc=[normalising], casefold=[uppercasing]} -- where removing the rule recovers real detections at zero precision cost. Every other row in the table recovers recall only by reintroducing false positives on every clean row, which is not detection, it is the verifier flagging everything and being right by accident.

The pure cases are the ones that matter, and they are the indictment. `casefold` exists because the target collation is case-insensitive, which is true. It costs 25 of 26 detections against a migrator that upper-cases every customer name, and it costs them silently and for free -- there is no false-positive penalty to notice, no noisy output to investigate, nothing at all to suggest the verifier has stopped looking at that column.


## 5. The check no row comparison can perform

Ask each engine, in its own collation, how many distinct names it holds. This is not a comparison of values; it is a question about the index, and it is the only check here that can see `COLLATION_FOLDS_LESS`.

> **Predicted (P4)** -- The distinct-name check will detect the collation divergence that every row-level comparison misses, and will keep working across the defective migrators.

| migrator | source distinct | target distinct | check | ground truth |
|---|---|---|---|---|
| `faithful` | 26 | 27 | diverges | 2 rows corrupted |
| `trimming` | 26 | 26 | **agrees** | 3 rows corrupted |
| `normalising` | 26 | 26 | **agrees** | 3 rows corrupted |
| `rounding` | 26 | 27 | diverges | 3 rows corrupted |
| `numeric-account` | 26 | 27 | diverges | 2 rows corrupted |
| `uppercasing` | 26 | 26 | **agrees** | 26 rows corrupted |

> **Contradicted (P4)** -- It detects the collation divergence -- 26 against 27 over rows that are byte-identical, which no value comparison in this project can reach. And then it is defeated by 3 of the 5 defective migrators, for a reason worth sitting with: those three all damage the `name` column, and the damage merges a pair of names, and the merge removes exactly the one distinct value the collation difference had added. The counts come back equal. Two independent defects cancel, and the check reports a clean migration precisely when two things are wrong instead of one.

An aggregate is a lossy summary and lossy summaries admit collisions. This one is not a contrived collision -- the two defects are causally unrelated and extremely common, and they cancel because both act on the cardinality of the same column. Any check that compares two numbers rather than two sets has this shape. Comparing the *sets* of distinct names, rather than their counts, catches all six cases; it also costs memory proportional to cardinality, which is why nobody does it.


## 6. Copy migration and dual write are different problems

During the backfill every value passes through the source first, so the source's own coercions are applied to both sides and cancel. Once dual write is switched on the application writes the same value to both engines independently, each coerces it its own way, and nothing cancels.

> **Predicted (P5)** -- The two scenarios will expose the same hazards, so a verifier calibrated during the backfill will carry over to dual write.

| column | copy migration | dual write |
|---|---|---|
| `name` | 0 | 0 |
| `code` | 0 | 0 |
| `account` | 1 | 1 |
| `amount` | 1 | 1 |
| `active` | 0 | 0 |
| `seen` | 0 | 29 |

With the full rule set: copy migration tp=2 fp=0 fn=0 tn=27 precision=1.00 recall=1.00
With the full rule set: dual write     tp=2 fp=27 fn=0 tn=0 precision=0.07 recall=1.00

> **Contradicted (P5)** -- The same rule set that gives precision 1.00 on the copy gives 0.07 on the dual write. The `seen` column is the reason: in the copy it arrives at the target as epoch milliseconds, which the `temporal` rule was written for, and in the dual write the application binds it as a string, which the rule does not recognise. Same data, same rule, and the rule only works on one of the two paths.

The operational consequence is specific. Verifier tuning happens during the backfill, because that is when there is time. Dual write is switched on at the start of the cutover window, at which point the verifier that has been quiet for three weeks starts objecting to every row, at two in the morning, with everyone watching. It will be assumed to be broken, because for three weeks it was right and now it is screaming. It is not broken. It has been pointed at a different problem.


## 7. The online backfill

Copy in id order, in batches, while the application keeps writing. The watermark pattern is correct for inserts and wrong for updates: a row updated after the batch that copied it stays stale, and no row is missing, so the count agrees.

> **Predicted (P6)** -- The row count will agree while rows are stale, and the modification-stamp second pass will fix all of them.

| run | stale rows | source count | target count | count check |
|---|---|---|---|---|
| watermark only | 4 | 29 | 29 | **passes** |
| watermark + second pass | 0 | 29 | 29 | passes |

> **Held (P6)** -- The count agrees in both runs -- 29 rows on each side -- while 4 rows are stale, which is the whole reason a count is not a verification. The second pass does fix all of them, here, because the modification stamp in this harness is updated by the same code that performs the write and therefore cannot be forgotten. In a real system it is updated by a trigger, or by an ORM hook, or by whichever of the four services writing to that table remembered to. The second pass is exactly as reliable as the least disciplined writer.

Stale rows [2, 3, 15, 27] are ids updated after their batch had passed. Id 2 was written during batch 0 and copied in batch 0, so it is fine; the ordering within a batch decides, and the ordering within a batch is not something the watermark records.


## 8. The timestamp that no longer means anything

The target stores `seen` as epoch milliseconds. The conversion from the source's wall-clock TIMESTAMP is performed by the JDBC driver using the JVM's default time zone, and the time zone is not written down anywhere in the target.

> **Predicted (P7)** -- Running the same migration under different default time zones will produce different bytes in the target, making the migration non-deterministic.

> **Contradicted (P7)** -- Wrong, and wrong in a way that took a while to accept. The stored value is 1705309200000 under all three zones. `TimeZone.setDefault` after the JVM has started does not reach the drivers, so this experiment cannot settle the question in-process; it would need three separate JVMs started with `-Duser.timezone`. What it does establish is that the result is stable within a process, which is what makes this document reproducible. The claim about cross-machine determinism is unproven here and is not made.

> **Predicted (P8)** -- The `temporal` rule -- the one rule without which no rule set is usable at all -- will nonetheless report the timestamp column as correct even though the target no longer records what the source's wall clock said.

Row 23 was stored in the source as `2024-03-31 02:30:00.0` -- a wall clock with no zone, which is what `TIMESTAMP` means. The target holds `1711852200000`. Read back, that integer says:

| read in zone | wall clock recovered |
|---|---|
| UTC | 2024-03-31T02:30 |
| Europe/London | 2024-03-31T03:30 |
| America/New_York | 2024-03-30T22:30 |
| Pacific/Kiritimati | 2024-03-31T16:30 |

> **Held (P8)** -- The `temporal` rule reports 0 differences on the `seen` column. It converts the source's Timestamp to epoch milliseconds using the same default zone the driver used on the way in, so the two conversions cancel and the column is certified correct. The verifier is not wrong about anything it was asked; it inverts the exact transformation it should be interrogating.

The chosen row is `2024-03-31 02:30:00`, which does not exist in Europe/London -- the clocks go forward at 01:00. The source accepted it because TIMESTAMP has no zone and therefore no opinion. The target accepted it because it has no date type at all. The verifier certified it. It will be found by an accountant, in a report that sums to the wrong day.

This is the sharpest form of the blindfold result. `temporal` is not an optional convenience: section 3 shows it is one of the three rules without which the verifier is unusable. The rule you cannot operate without is the rule that conceals the defect you would least like to ship.


## 9. A gate that refuses an absence of evidence

"The verifier reported no differences" is consistent with a correct migration and equally consistent with a verifier that cannot detect anything. Both produce the same clean report. So the gate plants known defects and requires the verifier to be observed catching every one before its silence is allowed to mean anything -- mutation testing, applied to a migration.

One detail decides whether the gate is worth anything. A control counts as caught only if the verifier flags a row *that this control damaged*. An earlier version of the gate credited a control whenever the verifier reported anything at all, and every control passed -- because this corpus contains two rows that the engine pair corrupts under any migrator, so the verifier was never silent and the gate never learned anything. A positive control that is positive whatever you do is not a control.

> **Predicted (P9)** -- The full rule set will pass the gate: it has perfect precision on the faithful migration, which is the configuration anyone would ship.

| verifier | decision | controls caught | reason |
|---|---|---|---|
| `row-count` | NO_GO_BLIND | 0/5 | caught 0 of 5 planted defects; its silence on the real data means nothing |
| `naive-checksum` | NO_GO_UNUSABLE | 5/5 | objects to 93% of rows; above the 5% ceiling its output is not triaged |
| `canonicalising[trim+nfc+numeric+boolean+casefold+temporal+identifier]` | NO_GO_BLIND | 3/5 | caught 3 of 5 planted defects; its silence on the real data means nothing |
| `canonicalising[trim+nfc+numeric+boolean+temporal+identifier]` | NO_GO_BLIND | 3/5 | caught 3 of 5 planted defects; its silence on the real data means nothing |
| `canonicalising[numeric+boolean+temporal]` | NO_GO_UNUSABLE | 5/5 | objects to 93% of rows; above the 5% ceiling its output is not triaged |
| `canonicalising[numeric+boolean+temporal+identifier]` | NO_GO_CORRUPTION | 5/5 | reported 2 differences on the real data |

> **Contradicted (P9)** -- It fails, and it fails as NO_GO_BLIND. The configuration with perfect precision on the faithful migration -- zero false positives, the one anybody would ship -- is caught by its own controls missing two of the five planted defects. Its silence on the real data was never evidence. The only configuration that catches every applicable control is the one built from the injective rules alone, which is the same set section 11 derives from first principles, and that configuration returns NO_GO_CORRUPTION: it finds the two genuinely broken rows and refuses the cutover. Two different NO_GOs, and the difference between them is the entire value of the gate. One says the data is bad. The other says you do not know.

Two rows of that table deserve a second look. Dropping `identifier` from the injective set moves the verifier from NO_GO_CORRUPTION to NO_GO_UNUSABLE at 93% of rows objected to -- the account column is a string on one side and an integer on the other, so without that one rule every row differs. And `naive-checksum` catches 5 of 5 controls. It is the most sensitive verifier here and it is worthless, because it also objects to 93% of the rows. Sensitivity alone is not a virtue; the gate needs both halves.

Note the row count: 0 of its controls caught, and it is quiet on the real data. A gate that had asked only "is the verifier quiet?" would have said GO. This is not a strawman -- a row-count reconciliation is what most cutover runbooks actually contain.

The two decisions the gate can return that a conventional check cannot are NO_GO_BLIND and NO_GO_UNUSABLE. The first is a verifier that found nothing and also could not find a planted defect. The second is a verifier objecting to more than 5% of rows, which is the point past which nobody triages the output and someone quietly raises the threshold. Both of those failures are, on any conventional dashboard, indistinguishable from success.


## 10. The hazard taxonomy, and the two that were dismissed

Eleven mechanisms, each measured against the real drivers before being written down. The column that matters is the last one: whether the mechanism destroys information or merely changes how it looks.

| hazard | mechanism | corpus rows | irreversible | corrupting here |
|---|---|---|---|---|
| `COLLATION_FOLDS_LESS` | target collation folds a smaller alphabet than the source's | 4 | yes | none |
| `SCALE_NOT_ENFORCED` | declared numeric scale is advisory, not enforced | 1 | yes | none |
| `DECIMAL_TO_BINARY_FLOAT` | exact decimal is stored as binary floating point | 3 | yes | 1 of 3 |
| `AFFINITY_COERCION` | type affinity rewrites a string as a number | 2 | yes | 1 of 2 |
| `CHAR_PADDING` | fixed-width columns pad on one engine only | 1 | no | none |
| `UNICODE_NORMALISATION` | the same grapheme has two code point sequences | 2 | no | none |
| `BOOLEAN_REPRESENTATION` | booleans render as TRUE/FALSE or as 1/0 | 1 | no | none |
| `TIMESTAMP_LOSES_TYPE` | the target has no date type; timestamps are text | 1 | no | none |
| `NULL_VS_EMPTY` | one engine conflates the empty string with NULL | 2 | yes | none |
| `TRAILING_WHITESPACE` | trailing spaces are significant on one side only | 2 | yes | none |
| `INTEGER_BOUNDARY` | an integer at the limit of the type's range | 2 | no | none |

> **Predicted (P10)** -- Whether a hazard corrupts is a property of the hazard, so each one can be classified once and handled the same way everywhere it appears.

> **Contradicted (P10)** -- No: [DECIMAL_TO_BINARY_FLOAT, AFFINITY_COERCION] are corrupting for some rows and harmless for others, with the same mechanism acting on both. `DECIMAL_TO_BINARY_FLOAT` destroys `99999999999999.1234` and leaves `0.10` perfectly recoverable, because a double carries about 15 significant decimal digits and the question is whether the value fits. `AFFINITY_COERCION` destroys `'0000007'` and leaves `'7'` alone. This is why the classification is per row rather than per hazard, and it is the practical reason migration test corpora fail to predict production: they are built from plausible small values, and every one of these hazards is harmless on plausible small values.

Two entries in the taxonomy were measured and did not fire. `NULL_VS_EMPTY` is real in other engine pairs and simply does not occur in this one -- both engines keep the empty string and NULL distinct. `INTEGER_BOUNDARY` is real in the opposite direction: H2's `INTEGER` is 32-bit and SQLite's is 64-bit, so the same type name means different widths, and the migration happens to run the widening way. Both are kept in the taxonomy and reported as negatives, because a taxonomy that only lists the hazards that fired in one experiment is a description of that experiment.

A third was removed. An earlier corpus put 2^63-1 into the account column and H2 threw `Data conversion error` on the insert. That is not a hazard; it is a hazard being handled. Every mechanism left in this taxonomy is one that lets the write succeed and the report come back green.


## 11. When is a canonicalisation rule safe?

Sections 3 and 4 leave an unsatisfying conclusion -- rules are necessary and rules are dangerous -- and it is unsatisfying because it is not actionable. There is a criterion, and it is the one already used to decide whether a hazard corrupts: injectivity.

A hazard corrupts when two source values land on one target value. A rule blinds when it maps two different values to one result, because from then on a defect that turns one of those values into the other is indistinguishable from no defect at all. The same test, applied once to the data and once to the verifier.

> **Predicted (P11)** -- Injectivity is a clean predictor: every non-injective rule will appear in the blindfold matrix as a pure blindfold, and no injective rule will.

| rule | columns | injective? | what it actually does |
|---|---|---|---|
| `trim` | `[name, code]` | **not injective** | declares two different values equal |
| `nfc` | `[name]` | **not injective** | declares two different values equal |
| `numeric` | `[amount]` | injective | reconciles two spellings of one value |
| `boolean` | `[active]` | injective | reconciles two spellings of one value |
| `casefold` | `[name]` | **not injective** | declares two different values equal |
| `temporal` | `[seen]` | **not injective** | declares two different values equal |
| `identifier` | `[account]` | injective | reconciles two spellings of one value |

Rules observed acting as pure blindfolds: [casefold, nfc]
Rules that are not injective:             [casefold, nfc, temporal, trim]

> **Held (P11)** -- No injective rule blinds the verifier anywhere in the matrix, which is the half of the claim that is worth something: it is a sufficient condition for safety, and it can be checked by reading the rule rather than by running an experiment. The converse does not hold. [temporal, trim] are not injective and were not observed blinding anything, because this corpus contains no defect that exercises them -- `trim` would hide a migrator that strips whitespace only if some *other* rule were not already catching that row on a different column. Absence from the matrix is a fact about the corpus. Non-injectivity is a fact about the rule.

The practical form of this is a review question rather than a test suite. For each canonicalisation rule in a migration verifier, ask: can two values that the business would consider different be mapped to the same thing by this rule? If yes, the rule has a blind spot, the blind spot is precisely the set of defects that produce that collision, and it needs a compensating check elsewhere -- a set-level cardinality comparison, a sample audited by hand, a planted control.

`temporal` is the case that makes the criterion earn its keep. It looks injective, it is injective as a function on longs, and it is the rule the verifier cannot operate without. It is unsafe because the conversion it inverts depends on a time zone recorded in neither database, so the property that matters is not injectivity of the rule but injectivity of the rule composed with the pipeline's own transformations. Checking a rule in isolation is not enough, and section 8 is what that costs.


## Scoreboard

| id | prediction | outcome |
|---|---|---|
| P1 | The row-count check will catch nothing, and the naive checksum will do better than it by catching some things and missing others. | **contradicted** |
| P2 | False positives will fall off gradually as rules are added, so an engineer tuning the verifier gets steady feedback and can stop when it is quiet enough. | **contradicted** |
| P3 | Each rule will blind the verifier to the one migrator it corresponds to, and removing the rule will restore detection without other effects. | **contradicted** |
| P4 | The distinct-name check will detect the collation divergence that every row-level comparison misses, and will keep working across the defective migrators. | **contradicted** |
| P5 | The two scenarios will expose the same hazards, so a verifier calibrated during the backfill will carry over to dual write. | **contradicted** |
| P6 | The row count will agree while rows are stale, and the modification-stamp second pass will fix all of them. | held |
| P7 | Running the same migration under different default time zones will produce different bytes in the target, making the migration non-deterministic. | **contradicted** |
| P8 | The `temporal` rule -- the one rule without which no rule set is usable at all -- will nonetheless report the timestamp column as correct even though the target no longer records what the source's wall clock said. | held |
| P9 | The full rule set will pass the gate: it has perfect precision on the faithful migration, which is the configuration anyone would ship. | **contradicted** |
| P10 | Whether a hazard corrupts is a property of the hazard, so each one can be classified once and handled the same way everywhere it appears. | **contradicted** |
| P11 | Injectivity is a clean predictor: every non-injective rule will appear in the blindfold matrix as a pure blindfold, and no injective rule will. | held |

11 predictions registered before measurement; 3 held, 8 contradicted.
