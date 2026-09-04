# The 2.4%

A 40-step workflow that issues one refund has 42 places it can die. I crashed it at all of
them.

Forty-one were boring. The journal either had the outcome written down, in which case
replay handed it back and the workflow carried on, or it had nothing, in which case the
refund had never started and starting it was safe. No policy, no reasoning, no operator.
Those crash points do not need durable execution. They need an append-only log and the
patience to re-run the function.

One was not boring. Between "tell the gateway to refund" and "write down that it did"
there is a gap, and a process that dies in that gap leaves a journal saying *I was about
to move money and I do not know if I did*. That is 2.4% of the failure surface, and it is
the entire reason the field exists.

I find this framing more useful than the one I started with. The pitch for durable
execution is usually "your workflow survives crashes", which is true and which sounds like
it applies uniformly across the run. It does not. The value is concentrated in a specific,
countable, *small* set of moments, and everything else the runtime does — the replay, the
journal, the determinism rules — exists to get you to those moments in a known state.

Which reframes the engineering question. It is not "how do I make my workflow durable".
It is:

**Where are my effect windows, how many are there, and what happens in each one?**

That question has an answer you can write down. The refund workflow has one effect, so it
has one window, and the answer is in ADR 005: retry with the recorded key, which is safe
if and only if the gateway deduplicates. A workflow with five effects has five windows and
needs five answers, and they may not be the same answer — retrying a refund is not the
same risk as retrying a "send the customer an email".

The corollary is the part I would actually take to a design review. If you cannot point at
your effect windows, you do not have a durability story; you have a library. And if your
mitigation is not aimed at one of those windows, it is aimed at a problem that solves
itself.

---

The other thing the sweep taught me is how differently the four policies look on paper
versus in the table. On paper, `escalate` — refuse to guess, stop the run, page a human —
is obviously the safest. In the table it recovers 41 of 42 rather than 42 of 42, and the
one it does not recover becomes a ticket. At a hundred runs a day that is a rota. At ten
thousand it is a team, and the failure mode of an under-resourced escalation queue is a
refund that never arrives, which is a worse customer outcome than the duplicate you were
avoiding.

I wrote down "escalate is strictly safer at no cost" as prediction 5 before running
anything. The scoreboard marks it contradicted. It was not wrong about the safety; it was
wrong about the cost being zero, which is the way this kind of prediction is usually
wrong.
