# Design walkthrough

How the pieces fit, and why each boundary is where it is.

## The shape of the problem

A semantic cache is three decisions wearing one name:

1. **What counts as similar** — the encoder and the score.
2. **What counts as similar enough** — the admission rule.
3. **What is allowed to share an answer at all** — the scope.

Most implementations collapse all three into one tunable float. The central
claim of this project is that they are different kinds of problem with different
kinds of solution, and that the collapse is why semantic caches return wrong
answers.

| decision | mechanism | why it cannot be the others |
|----------|-----------|-----------------------------|
| similar | pooled TF-IDF cosine | a *score* — continuous, comparative |
| similar enough | threshold + top-K veto | a *rule* — the veto must be able to overrule the score, so it cannot be a term inside it |
| allowed to share | tenant partition | a *key* — identical text with different answers is not a scoring problem at all |

Section 1 of the report proves the first two cannot be merged: for
L2-normalised pooled vectors the cosine is exactly the shared feature mass, so a
single decisive token's influence shrinks as the document grows. A threshold on
that score cannot express "these two are identical except for the one word that
matters".

## Package boundaries

```
corpus ──────────► ground truth. Imported by tests and by the scoring
   │               path only. Never by an admission decision.
   │
lexicon ─► embed ─► cache ─► gateway ─► report
                              ▲
                   workload ──┘        flight (concurrent, standalone)
```

The boundary that matters most is **`corpus` must not reach an admission
decision**. `cache.Lookup` takes `(text, tenant)` and returns an entry; it has
no access to the asking request's intent. `gateway.serve` reads `r.Intent` only
in `score()`, after the decision. Two tests enforce this by scrambling labels
and requiring the decision sequence to be unchanged — if either fails, every
number in the report is fiction, which is why they are the first tests in the
README's table.

`flight` sits outside the pipeline deliberately. The gateway is a discrete-event
simulation and single-threaded; `flight` is real concurrent code with real
goroutines. Mixing them would make the simulation non-deterministic for no
measurement benefit. Section 5 runs `flight` as a live demonstration instead, so
the concurrent design is exercised rather than merely described.

## Why a discrete-event simulation

The gateway advances a virtual clock over a pre-generated arrival list rather
than using real time and goroutines. Three reasons:

**Determinism.** A wall-clock version would produce a different report on every
run and on every machine. The findings here are differences of a few percent
between arms; scheduling noise would swamp them.

**The coalescing window becomes explicit.** In virtual time, "did request B
arrive while request A was in flight" is an interval comparison, not a race.
This made the project's worst bug findable: the cache was being filled at
request *arrival* rather than at backend *completion*, which silently removed
the entire window coalescing exists to cover. Coalesced counts were zero for
every mode and it looked plausible. In a wall-clock implementation that bug
would have presented as "coalescing doesn't help much", and been believed.

**The backend is an oracle.** It always answers the question asked. So every
wrong answer in the report is attributable to the cache or the coalescer and
nothing else — no model error, no flakiness, no retries. That attribution is
what makes precision meaningful.

The mechanism is a `pendingPut` queue drained by `retire(now)` in arrival order.
Entries become visible at their completion time, not before.

## The report is the artefact

`cmd/semcache` is not a demo harness — it is the experiment, and
`docs/results.md` is the output. The structure of every section is:

```go
w.Expect("...a falsifiable prediction, written before the number exists...")
// ... measurement ...
w.Found("...what actually happened...")
```

This is enforced by convention rather than by types, and it earned its place:
seven genuine defects were found by a printed number contradicting a written
prediction, listed in the README. The two that mattered most — deferred cache
fill, and the guard's true cost — would both have shipped as plausible-looking
results.

The discipline has a second effect that is harder to quantify. Writing the
prediction first forces you to state *why* you expect the number, and a
prediction you cannot justify is a measurement you have not designed. Section 1's
first prediction ("similarity drops by the swapped token's mass share") was wrong
on 32 of 35 pairs, and the reason — swapping one *word* changes several
*features*, because the bigrams containing it change too — is a better result
than the original claim.

## Scoring the sweeps

Every parameter sweep is scored on **correct answers served from cache**, not
hit rate and not precision.

Hit rate is maximised by admitting everything. Precision is maximised by
admitting nothing. Either alone can be won by moving the threshold to an
extreme, which makes "we tuned it and it got better" unfalsifiable. Their
product cannot: it counts requests that avoided a backend call *and* were right.

This is the same reasoning that makes the best-point comparison in a grid search
untrustworthy — a single best cell rewards grid luck. Where the report quotes a
best setting it also shows the full sweep.

## What is deliberately not here

- **No ANN index.** Lookup is a linear scan. At ≤ 300 entries that is correct
  and keeps admission legible. An approximate index would change the results in
  a way nothing here measures: the guard would adjudicate a candidate that is
  not necessarily the best one.
- **No eviction.** Entries live forever. Real eviction interacts with the
  poisoning result in section 5 and would need its own study.
- **No HTTP.** The transport is not the subject and would add failure modes that
  obscure the ones being measured.
- **No transformer.** None is available offline. The limitations document
  states which findings survive a better encoder (the structural ones) and which
  do not (every absolute number).
