# ADR 006 — A false positive is a cost with a price, not a footnote

**Status:** accepted

## Context

Six scenarios run. Four are regressions. `healthy` is the null control. The sixth,
`input-shift`, is the one that decides the project: the traffic mix changes — more `billing`
questions, fewer `warranty` ones — the model is unchanged, and answer quality does not move.
It is a real distribution shift that is not a problem.

Any detector that watches the input or output distribution will see it.

## Decision

Alerting on `input-shift` is recorded in an explicit column, and
`evaluate.operating_cost()` returns the triple `(model_calls_per_day, input_shift_false_positives,
false_alarm_days_on_healthy)`. Set-cover minimises over that triple, not over coverage alone.

## Consequences

**It is the only column that separates the good detectors from the sensitive ones.** Three
detectors fire on `input-shift`: output PSI, input PSI, and output MMD. All three also score
well on the regressions. Without this column they look like the best detectors on the panel.
With it, they are the detectors that page you at 03:00 because marketing ran a campaign.

**It is what makes the sliced-PSI result surprising rather than obvious.** Conditioning on
topic is usually sold as a sensitivity trick — it undilutes a subpopulation regression that
the aggregate misses, and it does: sliced PSI is the only distributional detector that
catches `template-regression`. But conditioning also *removes the confound*, because a shift
in the topic mix changes the weights of the slices, not the distribution within any slice.
So sliced PSI is simultaneously more sensitive to the thing you care about and blind to the
thing you do not — it is the only two-sample distributional detector on the panel that
catches `template-regression`, and it never fires on `input-shift`. Sensitivity and
specificity are usually a trade; here they are the same
move, and the reason is that the aggregate was measuring a mixture and the slice measures a
component. That finding requires an `input-shift` column to exist.

**Pricing the false positive is what made greedy set-cover correct.** With coverage alone,
several minimum covering sets tie, and greedy picks whichever comes first — including sets
built from the three input-shift-sensitive detectors. Once cost is the triple, greedy's
choice matches `brute_force_covering_set` exactly: **self-similarity + sliced PSI**, which
covers all four regressions, costs zero model calls per day, and fires on neither
`input-shift` nor `healthy`. The agreement between greedy and brute force is asserted in
`test_evaluate.py` rather than claimed, because greedy set-cover is not generally optimal
and the equality is a fact about this cost function on this panel, not a theorem.

**The 24-call/day quality canary loses on price.** It detects three of four regressions
promptly and cleanly. It misses `template-regression` — not by statistical bad luck but by
a decision: `GOLDEN_TOPICS` covers four of six topics, and the regression lives in
`warranty`, which is not one of them. That is the honest failure mode of every golden-set
canary. You cannot sample your way to coverage of a subpopulation you did not think to
sample, and the subpopulation that breaks is, by selection, usually one you did not think
about. The canary also costs 24 model calls a day forever, so the cheaper pair wins on both
terms.

The `false-positives-are-free` mutant removes the input-shift term from `operating_cost`;
it is killed by the test asserting greedy and brute force agree.
