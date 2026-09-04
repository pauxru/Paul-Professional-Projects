# ADR 005 — The unknown window gets a policy, not a fix

**Status:** accepted

## Context

An effect is journalled in two parts:

```
effect.intent    name, idempotency key, payload   -- "I am about to do this"
effect.outcome   ok, response                     -- "this is what happened"
```

If the process dies between them, the journal says the refund was *attempted* and does not
say whether it *happened*. Recovery has to decide.

The measurement: **1 of 42 crash points**, 2.4% of the failure surface. Everything else is
trivial — outcome recorded, so replay returns it; no intent recorded, so the effect never
started.

I spent some time trying to close the window. It cannot be closed. Making the journal write
and the remote call atomic requires the remote system to participate in a transaction, and
it will not. Writing the outcome first is worse: now a crash means the journal claims a
refund that may never have been sent, and the failure mode moves from *maybe twice* to
*definitely lost*.

## Decision

Do not pretend it is fixable. Make it a named, configurable policy on the runtime, with
its consequences measured:

| policy | what it does | duplicate refunds | runs stopped |
| --- | --- | ---: | ---: |
| `retry-same-key` | re-issue with the recorded key | 0 (with a deduplicating gateway) | 0 |
| `retry-new-key` | re-issue with a fresh key | **1** | 0 |
| `escalate` | refuse to guess; fail the run for a human | 0 | **1** |

`retry-same-key` is the default. It is the only row that is both safe and automatic, and
it is safe **only because someone else's server deduplicates** — the
`honoursIdempotency: false` row produces a duplicate from identical code here.

## Consequences

**The choice is a business decision wearing an engineering costume.** A duplicate £5
refund is cheaper than an operator; a duplicate £50,000 wire transfer is not. The runtime
should not have an opinion on which, so it takes a parameter, and the results table gives
whoever chooses the numbers to choose with.

**`escalate` is not free.** It is tempting to read "0 duplicates" and default to it. It
recovers **41/42** rather than 42/42: one run in the sweep stops and waits for a human.
At scale that is a queue, a rota and a runbook, and the failure mode of an under-resourced
escalation queue is a refund that never arrives — which is a worse customer outcome than a
duplicate. The prediction that escalate was "strictly safer at no cost" is scored as
**contradicted** in the results for exactly this reason.

**Narrow the window rather than closing it.** The intent write and the remote call are
adjacent with nothing between them; the outcome write is the next statement after the
response. Nothing else — no logging, no metric emission, no retry sleep — is allowed
between them, because every instruction in that gap is more time in the only dangerous
part of the run.

**Make it visible.** `hasDanglingIntent()` detects the state from the journal alone, which
is what lets the sweep count the window rather than assume it. In a real deployment that
same predicate is the alert: *this run is in the unknown window*, which is a fact worth
paging on precisely because it is rare.

## What would change this

A gateway that supports a query-by-idempotency-key endpoint collapses this entirely: on
recovery, ask what happened instead of guessing. That is the right design and it is not
available from most payment providers, which is why this is a policy rather than a lookup.
