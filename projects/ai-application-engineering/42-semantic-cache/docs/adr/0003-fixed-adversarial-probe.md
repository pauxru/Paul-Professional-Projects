# ADR 0003 — Measure the cache with a fixed adversarial probe, not with production error rates

**Status:** accepted

## Context

The standard way to report semantic cache quality is a false-hit rate observed
on live traffic: of the requests served from cache, what fraction were wrong.

Section 2 of the report runs the **identical configuration** against two
generated streams:

| stream | false-hit rate |
|--------|----------------|
| benign (users ask about one country, one plan, one direction) | **0.00%** |
| trap-heavy (users ask about siblings within the same family) | **3.31%** |

The cache did not change. The threshold did not change. The encoder did not
change. The number moved by its entire range because the *questions* changed.

This is not a subtle bias. A false-hit rate is a weighted average over the
traffic mix, and the weights are set by customers. The metric therefore:

- **improves when customers ask easier questions**, so it looks best exactly when
  it is least informative;
- cannot be compared across two weeks, two tenants, or two deployments;
- gives no signal before launch, when there is no traffic;
- silently degrades when a new customer arrives with a harder question
  distribution — the failure mode it was supposed to catch.

An engineer who tunes a threshold against this number is fitting to last week's
customers.

## Decision

Measure the cache with a **fixed adversarial probe** that is a property of the
configuration, not of the traffic.

```
for each intent i:      warm the cache with one phrasing of i
for each other phrasing p of every intent j:
        ask p; record whether the cache answers with intent i ≠ j
```

The probe is deterministic, depends only on (corpus, encoder, threshold, guard),
and never on who called today.

## Evidence that it measures the right thing

**It is invariant to traffic mix.** The probe reads **2/100 on both streams** —
the benign one where the headline rate says 0.00%, and the trap-heavy one where
it says 3.31%. That is the defining property: the cache is the same, so the
measurement is the same.

**It is monotone in the parameter it should be monotone in.** Sweeping the
threshold alone:

| threshold | probe confusions per 100 |
|-----------|--------------------------|
| 0.85 | 0 |
| 0.75 | 0 |
| 0.60 | 2 |
| 0.45 | 6 |
| 0.30 | 11 |

A metric that responds smoothly and monotonically to the knob you turn is a
metric you can tune against. The headline rate does not have this property,
because moving the threshold also changes which requests are hits at all.

## Consequences

- **The probe is only as good as its corpus.** It measures confusions among the
  families someone thought to write down. A confusable family nobody anticipated
  is invisible to it — but it was equally invisible to the production metric,
  and at least the probe's blind spot is a file you can read and extend. Adding
  a family is a corpus change, and a new family that starts failing is a
  regression with a name.
- **It does not replace production monitoring**, it replaces production
  monitoring *as a quality metric*. Production error rates remain the right way
  to detect that something has broken. They are the wrong way to decide whether
  a threshold is safe.
- The probe belongs in CI. It is fast, deterministic, and fails loudly when an
  encoder upgrade or a threshold change moves the confusion count. `test.ps1`
  runs the whole report generation for this reason.
- The report keeps reporting the headline rate alongside the probe, because the
  contrast between the two is the argument.

## The same error, committed twice

Section 3b of the report is this ADR applied to the *fix* rather than the
problem. The top-K veto's cost was priced at one point of aggregate hit rate,
which reads as free; measured on the paraphrase population it adjudicates, it
rejects 81.4%. The aggregate is dominated by verbatim repeats in exactly the way
the false-hit rate is dominated by benign questions.

The general form: **any rate averaged over traffic you do not control is a
measurement of that traffic.** If you want to measure a system, hold the inputs
fixed.
