# ADR 004 — Suspension is an unwind, not a blocked promise

**Status:** accepted

## Context

An agent that refunds more than a threshold needs a human to approve it. The natural
implementation:

```ts
const decision = await approvals.waitFor(caseId);   // resolves in a day or two
```

This passes every test. Tests do not redeploy.

In production the process holding that promise is restarted — a deploy, an OOM kill, a
node drain — and the promise is gone. Not failed: *gone*. There is no rejection to catch
and no error to log. The run simply is not there any more, and nobody notices until a
customer asks where their refund went. A two-day approval window is a two-day window for a
restart, which is to say a certainty.

## Decision

`ctx.awaitSignal(name)` records `signal.awaited` in the journal and then **throws**. The
stack unwinds, `run()` catches it, and returns `{ status: 'suspended', waitingFor: name }`.
Nothing is held in memory.

Resuming is just running again. Replay reaches `awaitSignal`, finds the recorded
`signal.awaited`, looks for a matching `signal.received`, and either continues or unwinds
again.

## Consequences

**Suspension is not journalled.** An earlier version appended a `run.suspended` event, and
the journal grew by one event on every restart. A run waiting a week behind a restart loop
would have accumulated thousands of events describing nothing. Suspension is a *status*,
derivable from a journal that ends in `signal.awaited` with no matching
`signal.received` — it is not a decision, and only decisions belong in a decision log.
There is a mutant for this: duplicating the `signal.awaited` append must fail the suite.

Measured: **5 restarts survived, 0 steps re-executed, journal length constant at 37.**
The test suite pushes that to 20 restarts.

**The resume path had a real bug.** When the journal ends in `signal.awaited`, the runtime
originally unwound immediately — correct when the signal has not arrived, and catastrophic
when it has, because an *approved* refund would suspend forever. The fix is to re-read
`opts.signals` at that point and record a late arrival:

```ts
const late = self.opts.signals?.[name];
if (late !== undefined) {
  self.appendEvent({ type: 'signal.received', seq: self.seq, name, payload: late });
  return late;
}
throw new SuspendSignal(name);
```

This was found by an end-to-end smoke run, not by a unit test, because every unit test at
the time delivered the signal on the first attempt. It is now pinned by
`tests/signals.test.ts` and by the `a late signal is ignored on resume` mutant.

**A signal is a fact, not a channel.** `opts.signals` is a plain record of what has already
been delivered — the runtime does not subscribe to anything. Whatever stores approvals
(a table, a queue, a webhook handler) is responsible for durability; the runtime only asks
"has it arrived?" each time it runs. That keeps the runtime free of infrastructure and
makes the approval as durable as the store behind it rather than as durable as a process.

## What would change this

Nothing about the unwind. What a real deployment needs on top is a **lease**: two workers
resuming the same suspended run would both replay and both proceed. The journal's
sequence-gap check turns that into a crash rather than a double refund, which is the right
failure but not a good one.
