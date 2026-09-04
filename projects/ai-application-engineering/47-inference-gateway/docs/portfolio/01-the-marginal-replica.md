# Why the marginal replica is worth nothing

The most expensive reflex in infrastructure engineering is "latency is bad, add
capacity". It works. That is the problem — it works often enough that nobody
measures when it stops working, and fleets grow monotonically because every
individual decision to grow them was locally justified.

Section 9 of [`../results.md`](../results.md) measures it.

## The setup

A gateway at 95% of its measured capacity, missing its objectives. Three moves
are available: add replicas, reorder work, refuse work. Section 8 has already
established that reordering cannot change how much waiting exists — DRR improves
the interactive tenant's tail by 2.7× and moves total throughput by −1%. So the
real choice is capacity against admission control.

Every configuration sees the identical trace. The metric is **offered success
rate**: SLO-meeting completions divided by requests *offered*, counting every
rejection as a failure. Measuring over served requests instead lets a policy win
by refusing almost everything, which is the standard way admission control is
oversold.

## I predicted shedding would win. It lost badly.

The registered prediction was that deliberately shedding around five percent of
load would beat doubling the fleet, because the utilisation-to-latency curve is
hyperbolic and shedding moves you down the steep part for free.

| configuration | offered success rate |
|---|---|
| 1 replica, accept all | 67.0% |
| **2 replicas, accept all** | **100.0%** |
| 1 replica, best shedding policy | 89.3% |

The reasoning was backwards, and the arithmetic says exactly how. **Capacity is
multiplicative in μ.** A second replica moves utilisation from 0.95 to 0.48 —
right down onto the flat part of the curve. **Shedding is subtractive in λ.**
Refusing five percent moves utilisation to 0.90, which is still inside the knee.
To match the second replica by shedding, you would have to shed half the
traffic.

That is a satisfying result, and it points in the direction everyone's instinct
already points, which should be suspicious.

## The third replica is worth exactly nothing

| replicas | offered success rate | marginal gain |
|---|---|---|
| 1 | 67.0% | — |
| 2 | 100.0% | **+33.0** |
| 3 | 100.0% | **+0.0** |
| 4 | 100.0% | +0.0 |

The second replica is worth thirty-three points. The third is worth zero — not
"diminishing", not "less", *zero to the resolution of the measurement*.

This is the finding, and it is not a statement about the number three. **The
value of capacity is not a property of capacity. It is a property of where the
purchase lands you on the utilisation curve.** Below the knee, additional
replicas buy nothing measurable, because there is no queueing left to remove.

Both of my predictions in this section were wrong for the same reason: I was
treating "add capacity" and "shed load" as competing quantities of a single
substance, when what actually matters is the shape of the curve at the point you
are standing on. Correlated errors like that are what a wrong mental model looks
like from the inside.

## Why this is the expensive one

A fleet sized by the reflex that solved the last incident keeps growing long
after it has stopped helping — and every step is defensible in isolation,
because latency did improve the last time.

The measurement that distinguishes the two cases is not latency. It is
utilisation against *measured* capacity, and section 3 shows that most
organisations do not have that number: the textbook estimate overstates real
capacity by 27%, so a fleet believed to be at 0.79 is actually saturated.

Which produces an unpleasant sequence. You do not know your real capacity, so
you do not know your real utilisation, so you cannot tell whether you are at the
point where the marginal replica is worth thirty-three points or the point where
it is worth zero — and the observable symptom, latency, looks the same in both
cases until after you have paid.

## What to do instead

**Measure μ.** Not derive it. One saturation run — infinite backlog, measure the
drain rate — costs minutes and is the denominator of every capacity decision you
will make afterwards. `capacity::saturation_rps` is thirty lines.

**Plot attainment against ρ, not against replica count.** The knee is visible.
Section 4's table shows it clearly: 98.7% at ρ=0.75, 91.4% at 0.85, 73.8% at
0.95. Where you sit on that curve determines what the next replica is worth, and
it is the only thing that does.

**Expect the second replica to be worth a great deal and the fourth to be worth
nothing.** If the fourth appears to help, the third did not fix what you thought
it fixed — you are somewhere else on the curve than you believe, and that is the
finding, not the latency.

**Do not conclude that admission control is useless.** It is the only lever that
reduces total waiting when capacity is fixed, and section 9b shows the choice of
*which* requests to refuse matters more than how many — the obvious policy is
the worst one measured, and uniform random dropping beats it. But it is a lever
for when you cannot buy capacity, not a substitute for buying it when you can.
