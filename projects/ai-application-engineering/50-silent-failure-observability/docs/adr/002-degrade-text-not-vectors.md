# ADR 002 — Degrade the text, never the vectors

**Status:** accepted

## Context

Every scenario needs to make some detector's statistic move. The path of least resistance
is to perturb the embeddings directly: add a drift vector to a share of the day's rows and
watch PSI climb. It is a few lines, it is fast, and it produces a beautiful chart.

## Decision

Degradations are applied to `Turn.answer` — the string — and to nothing else. The
embedding layer is downstream of the simulator and knows nothing about scenarios. `stream.py`
imports `corpus`, and does not import `embedding` at all.

## Consequences

A simulator that injected drift into the vectors would be measuring its own arithmetic.
"PSI detects a shift of magnitude d in the embedding space" is a fact about PSI and linear
algebra; it is not evidence that PSI detects a model swap, because the interesting and
unknown part of that question is precisely *how much a real degradation moves the vectors*
— and injecting the answer removes it.

Injecting into the text preserves the whole causal chain: the words change, the character
n-grams change, the hashed columns change, the marginal distributions change, and PSI
moves — or does not. Several of the project's most useful results are cases where it does
not:

- `template-regression` changes 8% of answers completely, and aggregate output PSI never
  alerts. The injection is large; the dilution is larger.
- `retrieval-decay` swaps in a fluent answer drawn from a *different topic's* template
  pool, and MMD never alerts, because the day's answers are still drawn from the same
  global pool of answer templates and the joint distribution barely moves.

Neither of those findings could exist in a simulator that injected drift into the vectors,
because in such a simulator the effect size is a parameter rather than a consequence. The
whole point is to let the pipeline decide how visible a given textual change is.

The same reasoning drives two smaller decisions. Latency is drawn from a fixed Gaussian and
is statistically independent of degradation — a test asserts the mean latency of degraded
and healthy turns differs by under 40ms — because a cheaper model is usually *faster*, and
a simulator that made bad answers slow would collapse the entire project into a latency
alert. And `http_status` is 200 in every scenario, asserted by
`test_every_request_succeeds_in_every_scenario`, because the moment one is not, APM catches
it and there is nothing here worth measuring.
