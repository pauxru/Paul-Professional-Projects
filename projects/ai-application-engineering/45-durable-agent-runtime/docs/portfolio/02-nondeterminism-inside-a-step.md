# Nondeterminism inside a step is free

Every durable execution framework tells you the same thing: **workflows must be
deterministic**. Then it gives you a list — no `Date.now()`, no `Math.random()`, no
iteration over a `Set`, no reading a module-level cache — and leaves you to police it.

I have always found the rule unsatisfying, because taken literally it is enormous. Real
code reads clocks and generates ids constantly. A rule that forbids all of it is a rule
people work around rather than follow.

So I built ten workflows containing patterns I have personally written without thinking,
ran each one, and replayed it. Three of them did not diverge at all, and the reason turns
out to be the whole answer.

`uuid-as-idempotency-key` generates a random UUID. `float-accumulation-order` sums an
array whose order is chosen by a coin flip. Both are unambiguously nondeterministic. Both
replay perfectly — because in both cases the nondeterminism happens **inside a
`ctx.step` body**. The step ran once, its result went into the journal, and on replay the
runtime handed the journalled value back without calling the function. The random UUID is
now a fact about what happened, recorded, as durable as anything else in the log.

Compare `branch-on-wall-clock`, which reads the clock and uses it to decide *which step to
call next*. That one destroys the run, because replay walks a different path through the
journal and the recorded events stop lining up.

The rule is therefore not "workflows must be deterministic". It is:

> **Nondeterminism inside a step is journalled and therefore free.**
> **Nondeterminism between steps changes the sequence of operations and is fatal.**

This is a rule you can apply in a code review in about two seconds, which the original
cannot be. You are not looking for randomness. You are looking for randomness that
escapes a step body and reaches control flow.

---

It also explains why the runtime's API has the shape it does. `ctx.now()` and
`ctx.random()` exist as journalled primitives not because clocks and RNGs are special, but
because reading them is the most common way for a value to reach control flow without
passing through a step. Wrapping them makes the common case correct by default.

And it explains the one mistake the runtime removes entirely. `uuid-as-idempotency-key`
was in the corpus because I expected it to be the most dangerous entry — a fresh key on
every attempt is exactly the bug that causes double refunds. It came back inert, because
the workflow cannot supply a key: the runtime derives it positionally and ignores anything
in the payload. The class of bug does not exist in this design, so the experiment measuring
it has nothing to measure.

That is the outcome to aim for. A guard that catches a mistake is good. An API where the
mistake cannot be expressed is better, and the way you find out which one you built is to
write the bug on purpose and watch what happens.
