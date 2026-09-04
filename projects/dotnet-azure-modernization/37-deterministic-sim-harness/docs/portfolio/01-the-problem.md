# 01 — The problem: you cannot fix what you cannot reproduce

A distributed system fails in production every third Tuesday. The stack trace,
if there is one, points at code that is obviously correct. The logs from the
three services involved disagree about the order things happened in, because
their clocks disagree. You cannot reproduce it locally. You add logging and wait
another three weeks.

Everything about that loop is bad, but the specific thing that makes it bad is
that **the failing execution is gone**. It happened once, out of some enormous
number of possible interleavings, and nothing captured which one it was.

## Why the usual tools do not close this

**Unit tests** fix the interleaving to one the author thought of. That is their
job. The bug is in an interleaving nobody thought of.

**Integration tests with real networking** sample interleavings, but from a
distribution wildly biased towards the healthy case. A loopback network drops
nothing, reorders nothing, and has microsecond latency. The bug in this
repository shows up in **0 of 2,000 runs** on a healthy network and 46 of 2,000
on a hostile one. An integration suite is not a weaker version of this harness;
it is looking somewhere the bug is not.

**Chaos engineering** injects real faults into real systems, which is valuable
and which I am not arguing against. But when it finds something, you get an
incident, not a reproduction. You still cannot replay it.

**Record-and-replay** captures the execution, and then you have a trace file
that must stay in sync with a codebase that is changing underneath it, and that
is large, and that you have to store.

## The property that fixes it

Make the entire execution a pure function of a seed.

Not "seeded random test data" — the whole execution. Which message arrives
first. Which one is dropped. When the partition heals. What each node's clock
says. Which client is scheduled next.

If that holds, three things become true at once:

1. **A failure is a `u64`.** You put it in a test file. It is smaller than a
   log line.
2. **Replay is free.** No trace, no artefact, no storage. Run the binary again.
3. **Searching is embarrassingly parallel and cheap.** You can run 2,000
   executions in a second because there is no real network and no real time —
   the simulator does not *wait* 100ms, it sets a number to 100,000.

That third point is the one people underrate. The reason this finds a bug that
occurs in 2.3% of hostile runs is not that it is clever. It is that it can
afford to look 2,000 times.

## What it costs

Determinism is not free and it is not partial. One `HashMap` iteration, one
`Instant::now()`, one thread, and the property is gone — silently, because the
code still runs and the tests still pass and the seeds just quietly stop
reproducing.

So the boundary has to be explicit (ADR 0001) and defended by a test that fails
loudly when it is crossed (`tests/regression_seeds.rs` pins ten known-failing
seeds).

And the code under test has to be written for the harness. That is the real
price, and it is why FoundationDB wrote their own language to pay it. This
repository pays it the cheap way: the protocol is written against the harness's
interface, so what is verified is the protocol, not a production binary. See
`docs/known-limitations.md`.

## What is being demonstrated

Not "here is a simulator". The claim is narrower and checkable:

> There is a plausible protocol change that looks like an optimisation, passes
> every test on a healthy network, and is wrong. This harness finds it, tells
> you which operation is wrong and why, and hands you a seed that reproduces it
> forever.

Everything else in the repository exists to make that sentence true.
