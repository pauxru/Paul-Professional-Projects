# Prediction error is cheap for ordering and expensive for commitment

An inference gateway cannot know how long a request will run. Output length is
decided by the model, one token at a time, and the gateway finds out at the same
moment the user does. Every size-aware mechanism in the system therefore runs on
a guess.

The obvious question is how good the guess needs to be. Section 6 of
[`../results.md`](../results.md) measures it, and the answer is that the
question is malformed: it depends entirely on what the guess is used *for*.

## Two consumers, one predictor

The estimate feeds two mechanisms that have nothing in common:

**The scheduler** uses predicted cost to order the queue. Shortest-job-first
puts the cheap request in front of the expensive one, which is why a RAG query
arriving behind a batch job does not wait ninety seconds for it.

**The memory manager** uses predicted peak length to size the KV reservation at
admission. The gateway must decide whether a request fits before it knows how
big the request will become.

They are both "using the prediction". They are not remotely the same use.

## The first version of this experiment was confounded

I built the obvious thing: vary the estimator's error spread, watch SJF degrade,
report the accuracy threshold. The result was muddy — SJF's advantage decayed a
little, the effect was comparable to the noise, and the honest summary would
have been "prediction accuracy matters somewhat".

That would have shipped. It is a defensible-sounding finding, it matches
intuition, and there is nothing in it obviously wrong.

**The giveaway was FIFO.** FIFO's numbers moved too.

FIFO never consults the estimate for ordering — it serves in arrival order and
would produce identical output if the predictor returned random noise. If FIFO
is sensitive to estimator error, the error is reaching the system through some
channel other than scheduling. And it was: the KV reservation. One knob was
moving two mechanisms, and everything I had measured was their sum.

The experiment was fixed by splitting `estimator_spread` from
`reservation_spread`, which is a two-line change in `GatewayConfig` and took an
afternoon to justify.

## Separated, the two channels are not comparable

**Error in the scheduling estimate only:**

| estimate spread | SJF advantage over FIFO |
|---|---|
| 1.0 (perfect oracle) | +0.094 |
| 2.0 | +0.079 |
| 5.0 | +0.084 |
| 8.0 | +0.085 |

Flat. An 8× typical error costs essentially nothing.

**Error in the reservation estimate only:**

| estimate spread | SLO attainment | evictions | tail TTFT |
|---|---|---|---|
| 1.0 | 91.4% | 0 | 6.7 s |
| 3.0 | 79.7% | 106 | 56.0 s |
| 8.0 | 75.6% | 137 | 2 min |

The same error, applied to the other consumer, costs sixteen points of
attainment and multiplies tail latency by eighteen.

## Why

**Ordering needs a ranking, not a magnitude.** SJF only has to get the
*relative* order roughly right. On this workload a chat turn and a batch job
differ by more than an order of magnitude in cost, and multiplicative noise of
even 8× rarely swaps their positions. Rank statistics are robust to noise in a
way absolute quantities are not — this is the same reason median-of-ratios
normalisation survives outliers that break a mean.

**A reservation needs a magnitude, and it is a commitment.** Reserving too
little means the sequence outgrows its allocation and something has to give. The
gateway preempts, which throws away every token that sequence has already
generated. Preemption does not *correct* a bad reservation; it redistributes the
damage, and it destroys completed work every time it fires.

The asymmetry is between decisions that are **reversible** and decisions that
**commit resources**. Ordering is reversible: a request served in the wrong
position waits slightly longer and nothing is destroyed. A reservation is a
commitment against a fixed pool, and being wrong about it makes the pool
smaller for everyone.

## The rule

Not "improve the predictor". That is the expensive answer, and section 6 says
it would buy you approximately nothing on the scheduling side.

**Use predictions for ordering decisions, which are reversible and forgiving.
Avoid using them for resource commitments, which are neither.**

Where a commitment must be made from a prediction — and in a KV cache it must —
bias it in the direction whose failure mode is cheaper. Section 10 measures
that: when memory is the binding constraint, deliberately reserving **0.85× of
projected need** beats reserving 1.0×, because a larger batch is worth more than
the evictions it causes. When batch size is the binding constraint, memory was
never scarce, and the same knob turned the other way costs 33 points of offered
success for no benefit at all.

Which means the reservation factor cannot be tuned from a latency dashboard. You
have to know whether your batch is limited by memory or by configuration, and
those two states look identical from the outside.

## The methodological point

The confound was visible only because FIFO was in the table. Had the experiment
compared SJF against SJF at different accuracies — which is the natural way to
ask "how accurate does the predictor need to be" — the result would have looked
clean and been wrong.

**Keep a control that should not respond.** When it responds, you have found a
channel you did not know existed. That is worth more than the experiment you
were running.
