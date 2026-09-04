# Where inference-serving latency actually comes from

A discrete-event simulation of an LLM inference gateway, built to answer one question: when a serving system misses its latency objectives, is that a capacity problem or a queueing problem? The answer decides whether you spend money or spend thought, and the two are not interchangeable. Every figure below is produced by the simulator in this repository from a fixed seed, and each section states what it expected to find before reporting what it found.

**Predictions registered: 13 | held: 3 | contradicted: 10**

The contradicted ones are the useful ones. They are marked inline and discussed where they occur; none has been quietly rewritten to match the outcome.

A majority of the predictions in this document failed, which is a high enough rate to be worth explaining rather than boasting about. Two things produce it. The first is that the predictions were written to be falsifiable -- they name a direction *and* a magnitude, and several held in direction while failing badly on magnitude, which is scored here as a miss. The second is that the misses are not independent: sections 9 and 9a were wrong for one shared reason, stated there, and sections 6a and 6b were wrong for another. Correlated errors are what a wrong mental model looks like from the inside, and finding them is most of the value of writing the prediction down first.

---


## 1. Why batching works, and where it stops working

Decode is memory-bandwidth bound. Producing one token for one sequence requires streaming the model's weights out of HBM; producing one token for sixty sequences requires streaming them once. Step time is therefore `weight_load + per_seq * batch` with a small `per_seq`, and that shape -- a large fixed cost amortised across the batch -- is the entire reason continuous batching exists. It is not an optimisation applied to a working system. It is the difference between a system that serves concurrent traffic and one that does not.

| batch | step time | tokens/s | throughput vs batch 1 | per-request TPOT |
|---|---|---|---|---|
| 1 | 12ms | 82.0 | 1.0 | 12ms |
| 2 | 12ms | 161.3 | 2.0 | 12ms |
| 4 | 13ms | 312.5 | 3.8 | 13ms |
| 8 | 14ms | 588.2 | 7.2 | 14ms |
| 16 | 15ms | 1052.6 | 12.8 | 15ms |
| 32 | 18ms | 1739.1 | 21.2 | 18ms |
| 48 | 22ms | 2222.2 | 27.1 | 22ms |
| 64 | 25ms | 2580.6 | 31.5 | 25ms |

Going from batch 1 to batch 64 multiplies throughput by 31.5 while multiplying per-request time-per-output-token by only 2.03. That asymmetry is the whole business case. Note also the diminishing return: the marginal sequence added to a batch of 8 buys 63.9 tokens/s, while the marginal sequence added to a batch of 56 buys 22.1.

> The knee is not where you should operate. Throughput keeps rising past it, and so does queueing delay for anything that does not fit. The batch size worth running is the one your memory and your latency objective jointly allow, which is what the rest of this document is about.


## 2. Static batching against continuous batching

Under static batching a batch is formed, run to completion, and only then replaced. Its duration is the duration of its *longest* member, so one 900-token summarisation holds every other slot in its batch open for 900 decode steps -- including slots belonging to chat turns that finished after forty. Continuous batching retires each sequence as it completes and admits a replacement immediately.

**Prediction.** Continuous batching should roughly double throughput on this heavy-tailed workload, and improve the tail more than the median.

| batching | throughput (req/s) | TTFT p50 | TTFT p99 | median slowdown | SLO attainment |
|---|---|---|---|---|---|
| continuous | 3.14 | 94 ms | 6.7 s | 2.48 | 91.4% |
| static, batch 8 | 0.33 | 27 min | 55 min | 488.41 | 0.4% |
| static, batch 16 | 0.43 | 19 min | 41 min | 371.20 | 0.4% |
| static, batch 32 | 0.54 | 15 min | 31 min | 300.27 | 0.4% |
| static, batch 64 | 0.65 | 11 min | 24 min | 248.49 | 0.4% |

**Contradicted.** The prediction understated it. Continuous batching delivers 3.14 requests/s against 0.65 for the best static configuration -- a factor of 4.8, not a factor of two. Static batching cannot serve the offered load at all: at 3.86 requests/s offered it completes 0.65, so its queue grows without bound and its median time to first token is 11 min. SLO attainment is 0.4% against 91.4%. The comparison is not close enough to be interesting as a tuning decision, which is itself the finding: continuous batching is a precondition, not a knob.

Note the direction of the static-batching numbers as batch size grows: bigger batches help, because the throughput gain from amortising the weight load outweighs the extra head-of-line blocking. That is the right instinct applied to the wrong architecture. No batch size rescues it.


## 3. Measured capacity against assumed capacity

Capacity plans are usually written from peak decode throughput divided by mean output length. That estimate assumes the accelerator spends all of its time decoding at full batch. It does not: it also runs prefill, which is compute bound and blocks decode, and its batch is limited by KV-cache memory rather than by the configured maximum. Rather than argue about the size of the gap, this simulator measures it -- by handing the gateway an infinite backlog and watching how fast it drains.

**Prediction.** The naive estimate should be optimistic, but by a modest margin -- ten percent or so, small enough that a capacity plan built on it would be roughly right.

| replicas | measured capacity (req/s) | measured tokens/s | naive estimate (req/s) | overstatement |
|---|---|---|---|---|
| 1 | 4.54 | 1368.7 | 5.77 | 27% |
| 2 | 9.67 | 2085.1 | 11.53 | 19% |
| 4 | 18.51 | 2769.1 | 23.07 | 25% |

**Contradicted.** And in the direction that matters. The naive estimate overstates capacity by 27%. A team sizing a fleet from it would provision for 5.77 requests/s per replica and discover the real figure is 4.54 -- which means running at a true utilisation of 1.00 while believing they were at 0.79. Section 4 shows what the difference between those two numbers does to latency.

> Everything that follows is expressed as a fraction of *measured* capacity. Getting that denominator wrong by a quarter would move every experiment in this report into a different regime, which is the practical reason to measure it rather than derive it.


## 4. The utilisation curve

Queueing delay does not rise linearly with load. For an M/M/1 queue mean sojourn time is `1 / (mu - lambda)`, a hyperbola with a pole at saturation, and real systems inherit the shape even when they violate the assumptions. The practical consequence is that the same ten percent of extra traffic is free at one operating point and catastrophic at another.

**Prediction.** Latency should rise slowly to about 80% utilisation and then sharply. SLO attainment should stay above 90% through 0.85 and fall away by 0.95.

| utilisation | offered (req/s) | TTFT p50 | TTFT p99 | TPOT p50 | median slowdown | mean batch | SLO attainment |
|---|---|---|---|---|---|---|---|
| 0.40 | 1.82 | 52 ms | 708 ms | 17 ms | 1.37 | 14.1 | 99.6% |
| 0.60 | 2.72 | 57 ms | 815 ms | 21 ms | 1.72 | 25.0 | 98.9% |
| 0.75 | 3.41 | 63 ms | 943 ms | 26 ms | 2.10 | 36.3 | 98.7% |
| 0.85 | 3.86 | 94 ms | 6.7 s | 30 ms | 2.48 | 44.6 | 91.4% |
| 0.95 | 4.31 | 361 ms | 40.3 s | 31 ms | 2.74 | 47.7 | 73.8% |
| 1.05 | 4.77 | 2.0 s | 1 min | 32 ms | 3.26 | 49.3 | 47.1% |

**Held.** Attainment is 91.4% at 0.85 and 73.8% at 0.95, and tail time-to-first-token goes from 943 ms at 0.75 utilisation to 40.3 s at 0.95 -- a factor of 42.7 for a 27% increase in load. Note where the damage shows up: TPOT barely moves across the whole sweep, because adding sequences to a memory-bound decode batch is nearly free. It is TTFT that explodes, because that is where waiting lives. A dashboard tracking tokens per second would show this system getting *better* right up to the point users start leaving.

The mean-batch column explains the mechanism. Below saturation, extra load is absorbed by a larger batch, and the cost is spread thinly across every in-flight request as slightly slower decode. Once the batch reaches its memory or configuration limit there is nowhere left to absorb it, and additional load converts entirely into queueing. The knee in the latency curve is the point where the batch stops growing.


## 5. Head-of-line blocking, and what scheduling can do about it

Latency is the wrong axis for a heterogeneous workload. A 4000-token generation that takes ninety seconds is behaving perfectly; a forty-token chat turn that takes nine is a disaster; and the first looks ten times worse in a latency histogram. The tables below use *slowdown* -- observed latency divided by the time the request would have taken alone on the device -- which puts both on the same axis.

**Prediction.** Shortest-job-first should beat FIFO on mean slowdown. Aging, which promotes long-waiting requests to prevent starvation, should cost a little of that gain.

| policy | mean slowdown | chat p99 | rag p99 | summarise p99 | batch p99 | SLO attainment |
|---|---|---|---|---|---|---|
| fifo | 2.59 | 6.94 | 15.50 | 3.77 | 4.07 | 91.4% |
| sjf | 2.50 | 5.75 | 5.96 | 3.31 | 3.84 | 95.1% |
| class-priority | 2.55 | 6.20 | 9.09 | 3.33 | 6.22 | 94.1% |
| drr | 2.51 | 7.27 | 5.47 | 3.32 | 3.78 | 94.9% |
| sjf-aged | 2.57 | 5.86 | 16.72 | 3.36 | 5.00 | 92.6% |

**Contradicted.** Half of it held. SJF does beat FIFO on mean slowdown (2.50 against 2.59), and the effect is concentrated exactly where the theory says it should be: RAG requests, which are cheap to decode but arrive behind expensive ones, improve from a p99 slowdown of 15.50 to 5.96. But aging does not cost 'a little'. `sjf-aged` lands at 2.57 mean slowdown, and its RAG p99 of 16.72 is *worse than FIFO's*. The aging threshold is 3 seconds; at this utilisation almost every request waits longer than that, so nearly everything is promoted and the policy degenerates into FIFO with extra steps. An aging threshold has to be set relative to the wait the system actually produces, not relative to the wait you wish it produced -- and if you set it from the SLO, you will silently disable the policy you are aging.


## 6. What prediction error actually costs

Shortest-job-first needs to know how long a job will take, and an inference gateway cannot know: output length is decided by the model, one token at a time. Every size-aware policy therefore runs on a prediction. The obvious question is how much accuracy it needs.

The obvious experiment -- vary the predictor's accuracy and watch SJF degrade -- is confounded, and the first version of this section fell into it. The predictor feeds two consumers, not one: the scheduler uses it to *order* work, and the memory manager uses it to *reserve* KV cache. Varying one knob moves both. The giveaway was that FIFO's numbers also moved, and FIFO never consults the predictor for ordering at all. The two channels are separated below.

**Prediction.** SJF's advantage over FIFO should decay quickly as prediction error grows, and should be roughly gone once estimates are typically off by a factor of five.


### 6a. Error in the scheduling estimate only

| estimate spread | SJF mean slowdown | FIFO mean slowdown | SJF advantage | SJF attainment |
|---|---|---|---|---|
| 1.0 | 2.50 | 2.59 | +0.094 | 95.1% |
| 1.5 | 2.51 | 2.59 | +0.082 | 94.8% |
| 2.0 | 2.51 | 2.59 | +0.079 | 94.7% |
| 3.0 | 2.52 | 2.59 | +0.074 | 94.6% |
| 5.0 | 2.51 | 2.59 | +0.084 | 94.6% |
| 8.0 | 2.50 | 2.59 | +0.085 | 94.0% |

**Contradicted.** Decisively so. SJF's advantage is +0.094 with a perfect oracle and +0.085 when estimates are typically wrong by a factor of eight. It does not decay at all. The reason is that SJF needs the *ranking* to be roughly right, not the magnitudes: a chat turn and a batch job differ by more than an order of magnitude in cost, and multiplicative noise of even 8x rarely swaps their order. Rank statistics are robust to noise in a way that absolute quantities are not.


### 6b. Error in the memory-reservation estimate only

**Prediction.** Reservation error should matter less than scheduling error. A reservation that is somewhat wrong is corrected by preemption, which the engine already supports.

| estimate spread | mean slowdown | evictions | mean batch | TTFT p99 | SLO attainment |
|---|---|---|---|---|---|
| 1.0 | 2.59 | 0 | 44.6 | 6.7 s | 91.4% |
| 1.5 | 2.78 | 47 | 44.0 | 20.1 s | 84.9% |
| 2.0 | 2.84 | 57 | 43.6 | 22.0 s | 86.4% |
| 3.0 | 3.11 | 106 | 41.9 | 56.0 s | 79.7% |
| 5.0 | 3.20 | 142 | 39.2 | 1 min | 78.0% |
| 8.0 | 3.75 | 137 | 37.4 | 2 min | 75.6% |

**Contradicted.** And this is the section's real finding. Holding the scheduler on a perfect oracle and degrading only the *reservation* estimate takes attainment from 91.4% to 75.6% and tail TTFT from 6.7 s to 2 min, with evictions rising from zero to 137. Preemption does not correct a bad reservation; it redistributes the damage, and it destroys completed work every time it fires. So the two channels are not comparable: the same predictor error is nearly free when it decides *order* and expensive when it decides *how much memory to commit*.

> The engineering rule this yields is sharper than 'improve the predictor': use predictions for ordering decisions, which are reversible and forgiving, and avoid using them for resource commitments, which are neither. If a commitment must be made from a prediction, bias it in the direction whose failure mode is cheaper -- see section 10.


### 6c. Both channels, which is what a single predictor gives you

| estimate spread | mean slowdown | evictions | attainment |
|---|---|---|---|
| 1.0 | 2.50 | 0 | 95.1% |
| 2.0 | 2.66 | 50 | 90.9% |
| 3.0 | 2.61 | 60 | 91.1% |
| 5.0 | 2.91 | 119 | 86.8% |
| 8.0 | 2.90 | 102 | 88.3% |

The combined degradation tracks the reservation channel, not the scheduling one, which is what you would expect once you know the two are separable and one of them is nearly free.


## 7. Prefill against decode: the two objectives are in tension

Prefill is a large matmul over the whole prompt. It is compute bound, and while it runs no decode step runs, so every already-admitted request stalls. A scheduler must therefore choose: run prefill promptly and give newcomers a fast first token at the cost of stuttering everyone already streaming, or protect the streamers and make newcomers wait.

**Prediction.** Prefill priority should improve TTFT and worsen TPOT. The trade should look like a trade -- meaningful movement in both directions.

| priority | TTFT p50 | TTFT p99 | TPOT p50 | TPOT p99 | SLO attainment |
|---|---|---|---|---|---|
| prefill first | 94 ms | 6.7 s | 30 ms | 49 ms | 91.4% |
| decode first | 56 min | 114 min | 12 ms | 12 ms | 0.2% |

**Contradicted.** The direction held; the magnitude did not. Decode priority produces the best time-per-output-token in this entire report -- 12 ms against 30 ms, essentially the batch-1 floor, because the batch stays small and every step is cheap. It also produces a tail TTFT of 114 min against 6.7 s, and SLO attainment of 0.2% against 91.4%. This is not a trade-off curve, it is a starvation failure: with decode taking priority, prefill only runs when nothing at all can decode, which at this utilisation is almost never. The system optimises the metric it can see and destroys the one it cannot.

> Real systems resolve this with chunked prefill: a long prompt is split so it can be interleaved with decode steps rather than blocking them wholesale. That is not modelled here, and its absence is why the decode-priority case is as extreme as it is. It is included as a boundary rather than as a proposal.


## 8. Two tenants on one gateway

The workload has two tenants with deliberately different mixes. `interactive` sends mostly chat and RAG; `analytics` sends mostly summarisation and batch work. They offer the same request rate, but not remotely the same load -- and under FIFO, the tenant that behaves well subsidises the one that does not.

**Prediction.** Deficit round robin should protect the interactive tenant's tail latency without reducing total throughput, because scheduling reorders work rather than creating or destroying it.

| policy | interactive TTFT p99 | analytics TTFT p99 | ratio | throughput (req/s) |
|---|---|---|---|---|
| fifo | 4.7 s | 7.4 s | 1.6 | 3.14 |
| sjf | 2.8 s | 18.2 s | 6.5 | 3.11 |
| class-priority | 2.8 s | 15.3 s | 5.5 | 3.11 |
| drr | 1.7 s | 15.8 s | 9.3 | 3.11 |

**Held.** Deficit round robin takes the interactive tenant's tail TTFT from 4.7 s to 1.7 s -- a 2.7x improvement -- while total throughput moves from 3.14 to 3.11 requests/s, a change of -1.1%. That flat throughput line is the point: scheduling is a redistribution mechanism. It decides who waits. It cannot decide how much waiting there is.

> DRR charges each tenant by estimated *token* cost, not by request count. Charging by request would let the analytics tenant claim unbounded capacity simply by sending longer jobs, which -- given its mix -- is exactly what it would do.


## 9. Buying capacity against refusing work

This is the decision the report exists for. A system at 95% of its measured capacity is missing its objectives. The available moves are to add replicas, to reorder work, or to refuse some of it. Section 8 showed reordering cannot change how much waiting exists. That leaves capacity and admission control, and the two are usually compared on vibes.

Every configuration below sees the identical trace at 95% utilisation. The column that matters is *offered success rate*: SLO-meeting completions divided by requests **offered**, counting every rejection as a failure. Measuring success over *served* requests instead would let a policy win by refusing almost everything, which is the standard way admission control is oversold.

**Prediction.** Deliberately shedding around five percent of load at this operating point should beat doubling the fleet, because the utilisation-to-latency curve is hyperbolic and shedding moves you down the steep part for free.

| configuration | rejected | TTFT p99 | goodput (req/s) | attainment (served) | offered success rate |
|---|---|---|---|---|---|
| 1 replica, accept all | 0 | 1 min | 2.25 | 67.0% | 67.0% |
| 2 replicas, accept all | 0 | 598 ms | 3.77 | 100.0% | 100.0% |
| 3 replicas, accept all | 0 | 591 ms | 3.79 | 100.0% | 100.0% |
| 1 replica, slo-predictive | 380 | 5.4 s | 2.62 | 95.7% | 71.5% |
| 1 replica, queue-tokens | 191 | 6.5 s | 2.82 | 88.5% | 77.2% |
| 1 replica, cost-aware | 90 | 4.0 s | 3.22 | 93.2% | 87.6% |
| 1 replica, sjf + cost-aware | 88 | 3.4 s | 3.28 | 94.7% | 89.1% |
| 1 replica, drop 10% at random | 0 | 20.5 s | 2.85 | 88.4% | 79.5% |
| 1 replica, drop 25% at random | 0 | 786 ms | 2.75 | 98.8% | 74.1% |

**Contradicted.** Doubling the fleet wins outright: 100.0% offered success against 89.1% for the best single-replica configuration, and 67.0% for the baseline. Shedding cannot match it, and the reason is arithmetic I got backwards. Capacity is multiplicative in mu -- a second replica moves utilisation from 0.95 to 0.48, right down the flat part of the curve. Shedding five percent is subtractive in lambda and moves it to 0.90, which is still inside the knee. To match the second replica by shedding you would have to shed half the traffic.


### 9a. But the third replica is worth nothing

**Prediction.** Having been wrong about shedding, the natural correction is that capacity is simply the better lever. If so, a third replica should also help, if less than the second.

**Contradicted.** And again, in the opposite direction. The second replica is worth +33.0 percentage points of offered success. The third is worth +0.0. The value of capacity is not a property of capacity; it is a property of where the purchase lands you on the utilisation curve. Below the knee, additional replicas buy nothing measurable, and a fleet sized by the reflex that solved the last incident will keep growing long after it has stopped helping. Both of my predictions in this section were wrong for the same reason: I was treating 'add capacity' and 'shed load' as competing quantities of a single substance, when what actually matters is the shape of the curve at the point you are standing on.


### 9b. Which requests you refuse matters more than how many

**Prediction.** Among the shedding policies, the SLO-predictive one should win. It rejects precisely those requests whose predicted wait already exceeds their objective, which is the request most obviously worth refusing.

**Contradicted.** SLO-predictive is the *worst* admission policy measured. It rejects 380 of 1500 requests -- 25.3% of the traffic -- to reach an offered success rate of 71.5%, while cost-aware shedding rejects 90 (6.0%) and reaches 87.6%. Uniform random shedding at 25% reaches 74.1%, beating the clever policy while having no idea what it is doing. The defect is that SLO-predictive sheds whatever is *arriving while the queue is long*, and during a burst that is disproportionately interactive traffic -- the cheapest requests with the tightest deadlines, which are precisely the ones worth keeping. Refusing one batch job returns as much capacity as refusing twenty chat turns. Cost-aware shedding ranks candidates by urgency per token of work and drops the expensive, latency-tolerant end first; on this workload the value density of chat is about 664x that of batch, which is the whole margin.

The practical result: on one replica, refusing 5.9% of offered traffic by value density recovers 67% of the 33.0 percentage points that an entire additional replica buys, at no hardware cost. That is the honest version of the claim this section set out to make -- not that admission control replaces capacity, but that it is the cheapest way to spend the time before the capacity arrives, and that a poorly chosen shedding rule can be worse than a coin flip.


## 10. How much memory to set aside, and which constraint is binding

Admitting a sequence means committing KV cache for its whole lifetime. The gateway reserves `factor x projected peak footprint`. Above 1.0 it holds back headroom and runs a smaller batch; below 1.0 it over-subscribes, runs a larger batch, and risks having to preempt. The right value is not a constant.

**Prediction.** Over-subscribing memory should be a mistake: the batch gains are small and eviction throws away completed work, so the best factor should be at or slightly above 1.0 in every regime.


### 10a. KV capacity is the binding constraint (60k tokens)

| reservation factor | mean batch | evictions | rejected | throughput (req/s) | offered success rate |
|---|---|---|---|---|---|
| 0.50 | 27.1 | 1033 | 46 | 2.04 | 58.8% |
| 0.70 | 25.8 | 679 | 11 | 2.10 | 60.1% |
| 0.85 | 23.0 | 393 | 3 | 2.03 | 62.7% |
| 1.00 | 20.8 | 0 | 0 | 2.11 | 53.9% |
| 1.25 | 16.3 | 0 | 0 | 1.78 | 37.3% |
| 1.60 | 12.9 | 0 | 0 | 1.51 | 28.2% |


### 10b. Batch size is the binding constraint (160k tokens)

| reservation factor | mean batch | evictions | TTFT p99 | throughput (req/s) | offered success rate |
|---|---|---|---|---|---|
| 0.50 | 46.0 | 0 | 1.5 s | 3.18 | 95.7% |
| 0.70 | 46.3 | 8 | 1.9 s | 3.18 | 95.1% |
| 1.00 | 44.6 | 0 | 6.7 s | 3.14 | 91.4% |
| 1.30 | 37.7 | 0 | 57.8 s | 2.93 | 76.4% |
| 1.80 | 28.6 | 0 | 3 min | 2.55 | 62.5% |

**Contradicted.** In one regime only. Held where memory is plentiful and contradicted where it is not. When KV capacity binds, the optimum is at factor 0.85 -- deliberate over-subscription -- reaching 62.7% against 53.9% at full reservation. The larger batch is worth more than the evictions cost. When batch size binds, memory was never scarce, under-reserving is free, and over-reserving is pure loss: factor 1.8 shrinks the mean batch by a third and costs 33.2% of offered success for no benefit whatsoever. So the reservation factor cannot be tuned from a latency dashboard. You have to know whether your batch is limited by memory or by configuration, and those two states look identical from the outside.


### 10c. What preemption cannot fix

Building this section produced the simulator's worst bug, and the most instructive one. At reservation factors below 1.0 the run performed 19,997,889 evictions and completed nothing. Every sequence reserved less than it would need, so memory ran short; the gateway evicted a sequence, which freed a large block, which made the evicted sequence immediately admissible again; it was re-admitted, over-grew, and was evicted once more. The run only terminated because of an event ceiling, and it reported a throughput of 0.00 requests per second alongside a perfectly plausible 97.6% SLO attainment. Nothing in the output indicated the numbers were fiction.

Three separate defects had to be fixed. Admission has to reserve one token for every sequence already decoding, not just for the newcomer, or the gateway admits into memory its existing batch is about to consume. A preempted request has to back off before it can return, and the backoff has to be exponential, because linear backoff still lets a pathological configuration spend an entire run thrashing. And preemption needs a retry limit: a systematically insufficient reservation is not recoverable by rearranging which sequence is the victim, so after a few attempts the honest response is to reject the request. The report's thesis arrived here as a consequence rather than as a slogan -- when a system cannot fit the work, the only real options are more capacity or less work.

> The lasting fix was not any of those three. It was `RunStats::truncated`, which marks a run that hit the ceiling, and an assertion that no reported run is truncated. A safety valve that silently substitutes plausible numbers for real ones is more dangerous than no safety valve at all.


## 11. Auditing the simulator with Little's Law

`L = lambda x W`. The mean number of requests in a system equals the arrival rate times the mean time each spends there. It holds for any stable queueing system regardless of arrival distribution, service distribution, scheduling policy or server count -- it assumes almost nothing, which is what makes it useful as a check rather than as a prediction.

Because it must hold, it can be tested. This simulator measures `L` by integrating the in-system count over time inside the event loop, and measures `lambda` and `W` from the completion records. Those come from different code paths: occupancy from the clock advance, sojourn time from per-request timestamps. A bookkeeping error in the event loop breaks the identity and nothing else in the crate would notice.

**Prediction.** The identity should hold to within 2% across every configuration, with the residual coming from finite-run edge effects.

| configuration | measured L | lambda (req/s) | W (s) | lambda x W | relative error |
|---|---|---|---|---|---|
| fifo, rho 0.85 | 46.31 | 3.14 | 14.73 | 46.31 | 0.0066% |
| sjf, rho 0.85 | 45.58 | 3.11 | 14.67 | 45.58 | 0.0049% |
| drr, rho 0.85 | 45.69 | 3.11 | 14.69 | 45.70 | 0.0057% |
| fifo, rho 0.95 | 65.05 | 3.36 | 19.36 | 65.05 | 0.0053% |
| 2 replicas, rho 0.95 | 33.40 | 3.77 | 8.86 | 33.43 | 0.0813% |
| cost-aware shedding | 46.55 | 3.46 | 13.46 | 46.56 | 0.0078% |
| kv 60k, factor 0.85 | 65.01 | 2.03 | 31.97 | 65.00 | 0.0189% |

**Held.** With a great deal of room to spare: the worst disagreement across every configuration is 0.0813%. That is tighter than the 2% tolerance because the simulator drains rather than stopping at a horizon, so no request contributes to occupancy without also contributing a completion time.

This check earned its place. An early version of the event loop stepped every replica and then advanced a single global clock by the *minimum* elapsed time, which credited slower replicas with work they had not done. The multi-replica numbers looked entirely reasonable. Little's Law did not agree with them, and that disagreement was the only signal that anything was wrong.

> The general technique: find a quantity your system must satisfy for structural reasons, compute both sides from independent code paths, and assert they agree. It is the cheapest real check available on a simulation, and unlike a golden output file it keeps working when the results legitimately change.


## 12. What this does not model

The engine model captures three things: decode is bandwidth bound so batching is nearly free, prefill is compute bound so it blocks decode, and KV cache is the binding memory constraint. Those three determine queueing behaviour. A good deal else determines the constants, and none of it is here.

- **Chunked prefill.** Prefill runs to completion for one request before decode resumes. Real schedulers split long prompts so they interleave. This makes section 7's decode-priority case more extreme than any real system would be.
- **Paged attention and fragmentation.** KV cache is modelled as a single pool of tokens. Real allocators work in blocks and suffer internal fragmentation, so usable capacity is below nominal capacity by an amount that depends on the length distribution.
- **Tensor and pipeline parallelism.** A replica is one indivisible unit. Sharding a model across devices changes the constants and adds collective communication that this model has no representation for.
- **Speculative decoding and prefix caching.** Both change effective throughput substantially and neither changes the queueing structure -- which is precisely why leaving them out is defensible for these questions and indefensible for capacity planning.
- **Heterogeneous or degrading hardware.** All replicas are identical and stay that way. Real fleets contain a slow node, and the interesting failure is what a load balancer does when it finds one.
- **Request cancellation.** Users close tabs. A gateway that keeps generating for a disconnected client is wasting the scarcest resource it has, and the fix -- propagating cancellation into the decode loop -- is unglamorous and worth more than most scheduling work.

The conclusions that survive these omissions are the structural ones: that utilisation drives latency hyperbolically, that scheduling redistributes delay while only admission control reduces it, that prediction error is cheap for ordering and expensive for resource commitment, and that the marginal replica's value depends entirely on where it lands you. The conclusions that do not survive are any absolute number of requests per second.


---

Generated by `src/main.rs (cargo run --release --bin run_gateway)`. Environment: Rust 1.x, no external crates, single-threaded, deterministic. Content digest: `35bfb9e9e13bd844`.

Every number above is produced by the simulator in this repository from a fixed seed. Re-running the generator reproduces this file byte for byte, which `tests/results_integrity.rs` checks.
