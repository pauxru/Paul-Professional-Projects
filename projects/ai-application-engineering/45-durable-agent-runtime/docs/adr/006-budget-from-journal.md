# ADR 006 — The cost budget is re-derived from the journal

**Status:** accepted

## Context

An LLM agent needs a spend cap. The natural place to put it is a counter on the runtime:

```ts
this.spent += costCents;
if (this.spent > this.budget) throw new BudgetExceededError(...);
```

The runtime object is created when the process starts. It is the one thing in the system
that does not survive a crash.

## Decision

Before executing anything, walk the journal and re-accumulate:

```ts
this.spent = 0;
for (const event of this.journal.events()) {
  if (event.type === 'step.completed') this.spent += event.costCents;
}
```

Steps replayed from the journal are **not** charged again — they were charged when they
first ran, and the accumulation above already counted them.

## Consequences

Measured, with a workflow costing 200 cents under a 250-cent limit:

| counter | reported spend after one crash | trips the limit |
| --- | ---: | :---: |
| resets to zero on resume | **310** | **yes** |
| re-derived from the journal | 200 | no |

The naive counter reports 310 cents for 200 cents of work and aborts a run that was never
over budget. Three things make this worse than an ordinary off-by-one:

- **It fires on resumption**, so it looks like the crash caused the overspend. The
  postmortem goes looking for a runaway loop.
- **It fires hardest on the runs that crashed most** — the ones you most want to finish.
  A run that crashed three times reports roughly three times its true cost.
- **It is safe-looking.** Aborting when you think you are over budget is the conservative
  choice. Nobody reviews a budget check for being *too* strict, so this survives review
  indefinitely.

The counter is also the thing an operator reads during an incident, so it is not only a
control, it is evidence. An over-reporting counter makes a fleet look like it is burning
money it never spent.

Two mutants cover this: zeroing the re-accumulation, and charging replayed steps. Both are
killed.

## What would change this

`costCents` is a number the workflow declares, not a measurement. A real budget takes
token counts from the provider's response and journals those — which is the same design,
with the number coming from the `effect.outcome` payload instead of the step's argument.
Nothing about the re-accumulation changes; the source of the number does.

The other gap is that the budget is per-run. An agent fleet needs a shared cap, and a
shared cap needs a counter outside the journal — at which point it is a distributed
rate limiter and stops being this runtime's problem.
