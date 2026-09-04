# ADR 001 — The journal records decisions, not state

**Status:** accepted

## Context

A workflow that survives a crash has to get its variables back. There are two ways.

**Snapshot the state.** After each step, serialise the workflow's locals and write them
down. Recovery loads the snapshot and continues. This is what a checkpointing job scheduler
does, and it has the pleasant property that the workflow function can be as
nondeterministic as it likes.

**Record the decisions.** Write down what each step *returned*. Recovery re-runs the
workflow function from the top, and every step that has a recorded result returns it
without executing. State is reconstructed by re-execution.

## Decision

Record the decisions.

## Consequences

The immediate cost is the one this project spends most of its effort on: **the workflow
function must be deterministic between steps**, because replay depends on it producing the
same sequence of calls. That is a real constraint on ordinary code, and §3 of the results
measures how badly it is violated in practice.

What it buys:

- **The journal is small and does not grow with the workflow's data.** 37 events, 2709
  bytes, ~77 bytes per step, for a workflow that manipulates a refund case file. A
  snapshot would carry the case file every time.
- **The journal is legible.** Every line is a decision a human can read: this step
  returned this, this refund was attempted with this key, this approval arrived. During an
  incident that is the difference between an audit trail and a heap of serialised objects.
- **There is no serialisation problem.** Snapshotting requires every local to be
  serialisable, which is a viral constraint — it reaches closures, class instances, open
  handles. Recording results only requires the *results* to be JSON.
- **Replay is free.** Measured: **0 step bodies re-execute** on resume, and 175 cents of
  work is not repeated. The function re-runs; the work does not.

The determinism requirement is therefore not fastidiousness, it is the price of the other
four properties, and the honest thing is to price it rather than to assert it away. Hence
the corpus in `src/workflows.ts` and the finding that the dangerous violations are the
ones no test can see.

## What would change this

A workflow whose steps return large payloads inverts the economics — the journal would
carry more bytes than a snapshot. The fix there is to journal a *reference* to the payload
rather than the payload, which keeps the decision-log shape. Snapshotting only wins when
the workflow genuinely cannot be made deterministic, and at that point the honest move is
to stop calling it a workflow.
