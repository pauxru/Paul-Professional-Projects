# ADR 0003 — Nodes emit actions; the engine decides what happens

## Status
Accepted.

## Context

A node needs to send messages and set timers. The obvious design gives it a
handle to the simulator. In Rust that means `&mut Sim` while the simulator is
already holding `&mut self.nodes[i]`, which does not borrow-check, and the usual
escapes — `RefCell`, `Rc`, taking the node out and putting it back — trade a
compile error for a runtime one.

There is a design problem underneath the borrow problem. If a node can reach the
simulator, it can reach global time, other nodes' state, and the RNG. Every one
of those is a way to accidentally write a protocol that only works in
simulation.

## Decision

`Ctx` collects an `Vec<Action>` and nothing else. A node can `send`, `timer` and
`trace`. It cannot see the queue, other nodes, or the fault model. The engine
drains the actions after the handler returns and applies the network model to
them.

The borrow works out because `Ctx` does not borrow the simulator at all:

```rust
let mut ctx = Ctx { me, now: skewed, global_now: self.time, peers, actions: vec![] };
self.nodes[ev.to].handle(&mut ctx, ev.event);   // borrows self.nodes
let actions = std::mem::take(&mut ctx.actions);
self.apply(ev.to, actions);                      // borrows self
```

`Ctx` exposes two clocks. `now` is this node's *skewed* view and is what
protocol logic must use. `global_now` is the engine's true clock and exists so
the harness can record a history with a consistent real-time order for the
checker — it is an oracle, and a node reading it would be cheating.

## Consequences

Fault injection is entirely the engine's business. Adding straggler links later
(ADR 0005) touched `apply` and nothing else; no protocol code changed.

The two-clock split is the part worth defending. A linearizability checker needs
a total real-time order over invocations and responses. Nodes cannot supply that
— they disagree about the time, which is the point of simulating skew. So the
harness has to observe it from outside, and the design has to make it obvious
which of the two clocks a given line of code is entitled to read.

The cost is a small amount of indirection: a node cannot observe the effect of
its own send within the same handler. That has not been a problem, and it
mirrors reality, where it is also true.
