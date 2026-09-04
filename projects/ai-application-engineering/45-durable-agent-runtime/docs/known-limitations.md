# Known limitations

What this project measures, and — more usefully — what it does not. Every number in
`docs/results.md` should be read against this list.

## The crash model is "the process died", not "the disk lied"

The journal is in memory. `serialise()` / `parse()` model durability, including a torn
final line, and `parse` deliberately stops at the first unreadable record rather than
skipping it. But nothing here calls `fsync`, so none of the interesting storage failures
are covered: a write that the OS acknowledged and lost, a page torn in the middle rather
than at the end, a filesystem that reordered two appends.

Those failures are real and they are somebody else's problem in a real deployment
(Postgres, DynamoDB, an append-only log service). The runtime's contract with the journal
is "appends are ordered and durable once they return", and that contract is assumed, not
tested.

## No real I/O, therefore no real ordering nondeterminism

Every effect is an in-process function call. This is why
`unordered-parallel-completion` came back **inert**: V8's microtask ordering is
deterministic, so a `Promise.race` between two already-settled promises resolves the same
way every time.

Racing genuine I/O is the one source of nondeterminism this harness cannot produce, and
it is a significant one — two concurrent HTTP calls completing in a different order on
replay would change the sequence of steps and be caught by the step-name guard, but a
`Promise.all` whose *results* are order-dependent would not be. The corpus is therefore
missing its most realistic entry, and the "4 of 10 caught in-process" figure would look
different with it.

## No concurrency, and no lease

One run at a time, one writer per journal. The sequence-gap check in
`InMemoryJournal.append` is the only defence against two processes resuming the same run,
and it is not enough: it turns a double execution into a crash *after* the second process
has already replayed and possibly re-issued an effect.

A real deployment needs a lease — a short-lived exclusive claim on the run, renewed while
it executes. That is the single largest gap between this and something you would run.

## One workflow shape

40 steps, one effect, one approval gate. The crash sweep is *exhaustive* over that
workflow — every journal write, no sampling — which is a strong claim about a narrow
thing.

The 2.4% unknown-window figure is the number that generalises worst. It is
`effects / journal_writes`. A workflow with five effects has roughly five times the
dangerous surface; a workflow that is mostly effects is nearly all dangerous surface. The
*shape* of the finding holds — the risk is concentrated in the effect windows and nowhere
else — but the percentage is a property of this workflow.

## Costs are declared, not measured

`ctx.step(name, body, costCents)` takes the cost as an argument. Nothing weighs anything.
The budget experiment is therefore a test of the *accounting*, not of the metering: it
proves the counter is wrong on resume, which is a real and independent bug, but a real
agent needs token counts from the provider.

## The nondeterminism corpus is a sample, not a survey

Ten patterns, chosen because I have written all of them. They are not weighted by
frequency, and there is no claim that they are the ten most common. The two useful
conclusions — that the dangerous class is *latent* rather than *detectable*, and that
nondeterminism inside a step is harmless — do not depend on the sample being
representative, because both are structural. The counts do.

## The in-process replay mode freezes the clock

`probe(entry, moveTheWorld = false)` pins `Date.now` to a constant. That is a deliberate
model of "a test replays on the next line", and it is also the only way to get a
deterministic result — with the real clock, `branch-on-wall-clock` was caught when the
machine was busy and missed when it was not.

The honest reading is that the in-process column is a *lower bound* on what a test suite
catches. A slow enough test suite catches more. That does not weaken the finding; a bug
whose detection depends on machine load is worse than one that is never detected, because
it gets a retry annotation instead of a fix.

## No type checking

See ADR 003. Types here are checked for syntax by Node's stripper and for nothing else.

## What would need to be true to run this for real

1. A journal backed by a real append-only store with an fsync contract.
2. A lease, so two workers cannot resume one run.
3. Effect handlers that make real network calls, with timeouts, and a `dangling intent`
   alert wired to a pager.
4. Token accounting from the model provider feeding `costCents`.
5. A `retry-same-key` policy validated against the *specific* gateway's idempotency
   semantics — including its key expiry window, which is the detail that quietly turns a
   safe retry into a duplicate three days later.
