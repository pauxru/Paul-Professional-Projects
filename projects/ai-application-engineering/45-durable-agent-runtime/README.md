# 45 — A durable agent runtime, and the one crash point that matters

An AI agent that spends money needs to survive a deploy in the middle of a refund. This
project builds the smallest runtime that can make that claim, then tries to break it at
every single point where it could break, and reports what it found.

The headline is not "durable execution works". It is **where** the risk actually lives:

> A 40-step workflow has **42 crash points**. Forty-one of them are trivial — the journal
> either records the outcome or records nothing, and replay is obvious either way.
> **One** of them, 2.4% of the failure surface, is the window between telling the payment
> gateway to refund and writing down that it did. Every retry policy, every idempotency
> key, every operational runbook exists for that single point. A mitigation aimed anywhere
> else is aimed at a problem that solves itself.

Everything below is measured by `src/experiments.ts` and regenerated into
[`docs/results.md`](docs/results.md) on every build.

---

## The story

I have twice watched an "agent" — one a payments retry job, one an LLM tool-calling loop
— get redeployed mid-run and do the thing it had already done. Both times the postmortem
landed on "we should add idempotency keys". Both times the fix was applied, and neither
postmortem established *where* the duplicate came from, which meant nobody could say
whether the fix covered it.

So this is that question, answered exhaustively rather than argued. Crash the process
before every journal write in turn. Recover. Count the refunds.

---

## What came out of it

**1. Exactly-once held at all 42 crash points — under exactly one set of assumptions.**

| unknown-effect policy | server deduplicates | recovered | duplicate refunds |
| --- | :---: | ---: | ---: |
| `retry-same-key` | yes | 42/42 | **0** |
| `retry-same-key` | **no** | 42/42 | **1** |
| `retry-new-key` | yes | 42/42 | **1** |
| `escalate` | yes | 41/42 | **0** |

Row 2 changes nothing in this repository. It changes the *remote system*. Exactly-once is
a property of the pair — runtime and gateway — and the runtime alone cannot promise it.
Row 3 changes one line here: generating a fresh key instead of reusing the recorded one.
That line looks correct in review.

**2. Nondeterminism inside a step is harmless; between steps it is fatal.**

Ten workflows containing patterns I have written without thinking. Four were caught by
replay. Two were **latent** — genuinely nondeterministic, invisible to an in-process
replay, and therefore reported clean by a test suite. One diverged **silently**, where no
guard can possibly see it. Three were **inert**, and that is the useful part: their
nondeterminism happened *inside* a `ctx.step` body, so it was journalled on the first
attempt and replayed verbatim.

That gives a rule you can actually apply in review, which "workflows must be
deterministic" does not:

> Randomness inside a step is recorded. Randomness between steps changes the sequence of
> operations and destroys the run.

**3. The latent class presents as a flaky test.** `branch-on-wall-clock` was caught when
the machine was busy and missed when it was not. That is not a bug report, it is a retry
annotation — and then it is load-bearing in production.

**4. A resumed run invents a budget overrun that never happened.** Work costing 200 cents
against a 250-cent limit reports **310 cents** after one crash, and aborts. The counter
lives on the runtime object, and the runtime object is the one thing that does not survive
a crash. It fails hardest on the runs that crashed most.

**5. The approval gate survives because it is not a promise.** `awaitSignal` unwinds the
stack rather than blocking. Five restarts, zero steps re-executed, zero journal growth.
An approval modelled as an unresolved promise passes every test, because tests do not
redeploy.

**Eight of thirteen predictions written before the experiments were contradicted**, and
the scoreboard in [§7 of the results](docs/results.md) says which.

---

## Running it

Node 22+ and PowerShell 7. **No dependencies, no package.json, no build step** — Node
executes the TypeScript directly by stripping types, and stage 5 of the test harness
fails the build if a non-builtin import ever appears.

```powershell
pwsh build.ps1     # loads every module, regenerates docs/results.md
pwsh test.ps1      # 6 stages: 143 tests, mutation testing, secrets scan
pwsh demo.ps1      # a five-minute tour that runs the real experiments
```

`test.ps1` includes a mutation stage. Eight single-line edits to load-bearing decisions —
removing the sequence-gap check, generating a fresh idempotency key, resetting the budget
on resume, ignoring a late signal — and all eight must make the suite fail. **8/8 killed.**

> The first version of that harness reported 8/8 while every mutant run had actually died
> on a module-resolution error before executing a single test. It now refuses to score a
> run whose suite did not start. A mutation score is worth exactly as much as the check
> that the mutant ran.

---

## How it is built

```
src/journal.ts       append-only event log; serialise/parse tolerating a torn tail
src/ledger.ts        a refund gateway that can be told to ignore idempotency keys
src/runtime.ts       replay, effects, signals, budget, nondeterminism guards
src/workflows.ts     the refund workflow and the 10-entry nondeterminism corpus
src/experiments.ts   the six experiments
src/predictions.ts   written first, scored mechanically, not edited afterwards
src/report.ts        generates docs/results.md and docs/results-stable.md
tools/mutate.ts      the mutation harness
```

The load-bearing decisions are in [`docs/adr/`](docs/adr):

1. [The journal records decisions, not state](docs/adr/001-journal-not-snapshot.md)
2. [Idempotency keys are positional, never generated](docs/adr/002-positional-idempotency-keys.md)
3. [No build step](docs/adr/003-no-build-step.md)
4. [Suspension is an unwind, not a blocked promise](docs/adr/004-suspension-is-an-unwind.md)
5. [The unknown window gets a policy, not a fix](docs/adr/005-unknown-effect-policy.md)
6. [The budget is re-derived from the journal](docs/adr/006-budget-from-journal.md)

What this deliberately does not show is in
[`docs/known-limitations.md`](docs/known-limitations.md) — no real I/O, no concurrency, an
in-memory journal, and one workflow shape. The security posture is in
[`docs/security-review.md`](docs/security-review.md).
