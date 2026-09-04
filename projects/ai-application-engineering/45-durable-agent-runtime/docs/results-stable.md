# Results

Every number below is produced by `src/experiments.ts` and re-derived when this file
is regenerated. Nothing is quoted from a previous run.

## 1. Exactly-once holds at every crash point, under exactly one set of assumptions

A complete run of the 35-step refund workflow journals **42 events**
and moves money **1** time. The sweep crashes the process before every one of
those 42 journal writes in turn, recovers from the surviving journal, and counts
the refunds.

| unknown-effect policy | server deduplicates | recovered | duplicate refunds | lost refunds | escalated |
| --- | :---: | ---: | ---: | ---: | ---: |
| `retry-same-key` | yes | 42/42 | **0** | 0 | 0 |
| `retry-same-key` | no | 42/42 | **1** | 0 | 0 |
| `retry-new-key` | yes | 42/42 | **1** | 0 | 0 |
| `escalate` | yes | 41/42 | **0** | 0 | 1 |

The first row is the headline: **every crash point recovers, and the refund is issued
exactly once.** The other three rows are why that sentence needs its qualifiers.

Row 2 changes nothing about this code. It changes the *remote system*: the gateway stops
honouring idempotency keys. A duplicate refund appears immediately. The key is a request
to somebody else to deduplicate, and if they decline, the guarantee is gone — so
"exactly-once" is a property of the pair, never of the runtime alone.

Row 3 keeps the deduplicating server and changes one line here: the retry after an
unresolved effect presents a *fresh* key instead of the recorded one. That is the
single most common way to get two refunds, and it is indistinguishable from correct
code unless you know why the key exists.

## 2. The whole risk is one crash point in forty-two

**1 of 42 crash points (2.4%)** land between an effect's
intent record and its outcome record — the window in which the runtime genuinely does
not know whether the refund was issued.

Every other crash point is trivial. The journal either contains the outcome, in which
case replay returns it, or contains no intent, in which case the effect never started.
Those 41 points need no policy, no reasoning and no operator.

The four rows in §1 differ **only** at that one point. Which means the entire
engineering argument about durable execution — the retries, the keys, the escalation
policy, the operational runbook — exists to handle 2.4% of the failure surface. That is
not an argument for skipping it: it is an argument for knowing exactly where it is,
because a mitigation aimed anywhere else is aimed at a problem that solves itself.

The window cannot be closed. A journal write and a remote call cannot be made atomic
without the remote system participating in a transaction, which it will not. What can
be done is to make the window *narrow*, make it *visible*, and give it a policy that
somebody chose on purpose. See docs/adr/002-unknown-effect-policy.md.

## 3. Nondeterminism inside a step is harmless; between steps it is fatal

Ten workflows, each containing a pattern I have written in non-durable code without
thinking about it. Each is run, then replayed twice: once immediately in the same
process (what a test suite does) and once after the wall clock and the RNG have moved
on (what recovery does).

| pattern | caught in-process | caught after the world moved | silently diverged |
| --- | :---: | :---: | :---: |
| `branch-on-wall-clock` | -- | yes | -- |
| `branch-on-math-random` | yes | yes | -- |
| `set-iteration-order` | yes | yes | -- |
| `module-level-cache` | yes | yes | -- |
| `uuid-as-idempotency-key` | -- | -- | -- |
| `unordered-parallel-completion` | -- | -- | -- |
| `exception-message-in-step-name` | yes | yes | -- |
| `clock-read-not-branched-on` | -- | -- | yes |
| `float-accumulation-order` | -- | -- | -- |
| `deterministic-control` | -- | -- | -- |

Three things fall out of this table, and only the first was expected.

**2 of 10 are latent.** They are genuinely nondeterministic and completely
invisible to an immediate in-process replay, because the test replays within the same
millisecond and reads the same clock. A durable-execution test suite reports them
clean. They surface for the first time in production, during recovery, which is the
worst possible moment to discover that replay does not work.

That "same millisecond" is not a modelling convenience — it is the finding. The
in-process column above is produced with the clock frozen, and freezing it was
forced: with the real clock, `branch-on-wall-clock` was caught when the machine was
busy and missed when it was not. A latent bug of this shape does not present as a
failure. It presents as a flaky test, gets a retry annotation, and is then load-
bearing in production.

**4 of 10 did not reproduce at all**, and the reason is the useful part.
`uuid-as-idempotency-key` and `float-accumulation-order` both contain real
nondeterminism — a random UUID, a float sum in an order chosen by a coin flip — and
both are harmless, because the nondeterminism happens *inside a `ctx.step` body*. The
result is journalled on the first attempt and replayed verbatim on the second. The
workflow never re-computes it.

So the rule is not "workflows must be deterministic". It is narrower and far more
useful:

> **Nondeterminism inside a step is journalled and therefore harmless.**
> **Nondeterminism between steps changes the sequence of operations and is fatal.**

That distinction is what makes durable execution usable by ordinary code. You do not
have to purge randomness from your workflow; you have to make sure it is on the
inside of a step. It also explains `unordered-parallel-completion` failing to
reproduce: V8 microtask ordering is deterministic, so a `Promise.race` between two
already-settled promises is not actually a source of divergence. The real hazard is
racing *I/O*, which this harness does not model — recorded in known-limitations.md.

## 4. A resumed run invents a budget overrun that never happened

The workflow costs **200 cents** to run once, under a limit of 250.
It crashes part-way and resumes.

| budget counter | reported spend after resume | trips the limit |
| --- | ---: | :---: |
| resets to zero on resume (the default everywhere) | 310 | **yes** |
| re-derived from the journal | 200 | no |

The naive counter reports 310 cents for work that cost 200, and aborts a run that
was never over budget. The failure is doubly bad: it fires on *resumption*, so it looks
like the crash caused the overspend, and it fires hardest on the runs that crashed most —
exactly the runs you most want to finish.

The fix is one loop over the journal before execution starts. It is easy to miss because
a budget is naturally modelled as a counter on the runtime object, and the runtime object
is the one thing that does not survive a crash.

## 5. What resumption costs and what it saves

| measure | value |
| --- | ---: |
| steps in the workflow | 35 |
| journal events for a complete run | 37 |
| step bodies re-executed on resume | **0** |
| work not repeated | 175 cents |

**Zero step bodies re-execute.** That is the claim durable execution makes and it is
measured by instrumenting the bodies rather than by trusting the design: a replayed
step returns its journalled result without calling the function, so any body that runs
during replay increments the counter.

The journal is small because it stores *decisions*, not state. Nothing snapshots the
workflow's variables; they are reconstructed by re-running the function, which is why
the determinism requirement in §3 is load-bearing rather than fastidious.

## 6. The approval gate survives restarts because it is not a promise

| measure | value |
| --- | ---: |
| first attempt suspended cleanly | true |
| journal length while waiting | 37 |
| restarts survived without progress or growth | 5/5 |
| steps re-executed across those restarts | 0 |
| resumed and completed once the signal arrived | true |

A human approval implemented as an unresolved promise passes every test, because tests
do not redeploy. It is lost by the first restart during the two days the human takes to
answer, and it is lost silently — the run simply is not there any more.

Here, `awaitSignal` **unwinds the stack**. The run is not blocked; it is over, and the
journal records that it was waiting. Restarting replays to the same point and stops
again, adding nothing to the journal — which is what the "0 growth over 5 restarts" row
is checking. A restart loop that grew the journal would eventually run out of disk while
appearing to work.

## 7. Predictions

13 predictions were written before any experiment was run. **8 were contradicted.**

| # | prediction | verdict | what actually happened |
| ---: | --- | --- | --- |
| 1 | With positional idempotency keys and a deduplicating gateway, every crash point recovers and the refund is issued exactly once. | held | 42/42 recovered, 0 duplicate refunds |
| 2 | The unknown window will be a substantial share of crash points -- more than one in ten -- because effects are the slow part of the workflow. | contradicted | 1 of 42 crash points (2.4%) land in the window |
| 3 | An idempotency key makes duplicate refunds impossible. | contradicted | 1 duplicate refund once the server stops honouring the key |
| 4 | Generating a fresh idempotency key on retry causes a duplicate refund. | held | 1 duplicate refund from a freshly generated key |
| 5 | The escalate policy is strictly safer at no cost: it still completes every run automatically. | contradicted | escalate recovered 41/42; 1 run stopped for a human |
| 6 | Every corpus pattern I labelled detectable is caught by an in-process replay. | contradicted | 4 of 7 predicted-detectable patterns were caught by an in-process replay |
| 7 | No pattern is invisible to an in-process replay but visible after the clock has moved; replay either diverges or it does not. | contradicted | 2 patterns invisible in-process and only visible once the clock moved |
| 8 | Every pattern in the corpus is a real hazard, since each one contains genuine nondeterminism. | contradicted | 4 patterns were harmless because the nondeterminism was inside a step body |
| 9 | A cost budget behaves the same whether a run is fresh or resumed. | contradicted | naive counter reports 310 cents against a 250 limit for work costing 200 |
| 10 | Resuming re-executes some step bodies -- the last few before the crash, at least. | contradicted | 0 step bodies re-executed on resume |
| 11 | A stack-unwinding approval gate survives repeated restarts without re-executing work or growing the journal. | held | 5/5 restarts survived, 0 steps re-executed, journal did not grow |
| 12 | The deterministic control workflow replays cleanly and is never flagged. | held | the control group replayed cleanly, as required |
| 13 | The crash-free control run moves money exactly once. | held | the crash-free control moved money 1 time |

## 8. What this does not measure

- **No real I/O.** Every effect is an in-process function call. Racing I/O completions
  is the one genuine source of ordering nondeterminism this harness cannot produce,
  which is why `unordered-parallel-completion` came back inert.
- **No concurrency.** One run at a time, no competing writers to the journal. The
  sequence-gap check in `InMemoryJournal.append` is the only defence against two
  processes resuming the same run, and it is not enough on its own — a real deployment
  needs a lease.
- **The journal is in memory.** `serialise`/`parse` model durability, including a torn
  final line, but nothing here fsyncs. The crash model is therefore "the process died",
  not "the disk lied".
- **One workflow shape.** 40 steps, one effect, one approval gate. The crash sweep is
  exhaustive over *that* workflow. A workflow with several effects has a wider unknown
  window, and the 2.4% figure would grow roughly with the number of effects.
- **Costs are declared, not measured.** `costCents` is a number the workflow states.
  A real budget needs token accounting from the provider.

