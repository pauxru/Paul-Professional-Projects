# ADR 002 — Idempotency keys are derived positionally, never generated

**Status:** accepted

## Context

Every side effect needs an idempotency key so the remote system can recognise a retry. The
obvious implementation is to generate one:

```ts
const key = crypto.randomUUID();
await gateway.refund({ ...payload, idempotencyKey: key });
```

This reviews perfectly. It is also the single most reliable way to issue two refunds.

The crash that matters happens *between* sending the request and recording the response.
On recovery the workflow re-runs, reaches the same line, and generates **a different UUID**.
The gateway sees a request it has never seen before, because as far as it can tell, it is
one. The money moves twice.

## Decision

The runtime derives the key and the workflow cannot supply one:

```ts
const ordinal = self.effectCounter++;
const idempotencyKey = `${self.opts.runId}:${name}:${ordinal}`;
```

Run id, effect name, and the effect's ordinal position within the run. Replay re-runs the
workflow from the top, so the counter lands on the same value, so the key is the same.

## Consequences

Measured in `tests/effects.test.ts` and §1 of the results:

- A retry after a crash in the unknown window presents the **same key**, and a
  deduplicating gateway collapses it to **one** movement of money.
- Switching to a generated key produces **1 duplicate refund** across the 42-point sweep.
  That is the `retry-new-key` row, and it is the only difference between it and the safe
  row.
- Two *deliberate* refunds in one run get different keys, because the ordinal differs.
  A mutant that drops the ordinal is killed by that test.
- A payload containing `idempotencyKey` is ignored. The workflow cannot opt out, which is
  why `uuid-as-idempotency-key` in the nondeterminism corpus came back **inert**: the
  runtime prevents the mistake by construction rather than detecting it.

The cost is that the key is only stable if the workflow reaches its effects in the same
order every time — the determinism requirement of ADR 001, arriving again in a different
costume. A workflow that calls an effect inside a branch on `Math.random()` gets a
different ordinal on replay, which is caught by the step-name guard before it can reach
the gateway.

## The thing this does not fix

A positional key is a *request* to the remote system to deduplicate. If the gateway
ignores it — which most internal services do, and every API written before someone got
paged about it — the key is a string that travels for no reason. The `honoursIdempotency:
false` row exists to make that measurable rather than assumed: **1 duplicate refund**, from
code that did not change.

This is why the README says exactly-once is a property of the pair. The runtime's job is
to make the retry *identical*; only the far side can make it *idempotent*.
