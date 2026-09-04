# Inference Gateway: a queueing model of LLM serving

A discrete-event simulator for an LLM inference gateway, written to answer one
question with evidence rather than intuition:

> When a serving system misses its latency objectives, is that a capacity
> problem or a queueing problem?

The answer decides whether you spend money or spend thought, and the two are
not interchangeable. [`docs/results.md`](docs/results.md) is the answer: twelve
experiments, thirteen registered predictions, **ten of them contradicted by the
measurements**, every figure computed at runtime from a fixed seed.

No dependencies. The RNG, the event loop, the percentile estimator and the
report writer are all in this crate, because a benchmark you cannot read end to
end is a benchmark you cannot trust.

---

## What it models

Three facts about accelerator inference determine everything about queueing
behaviour, and this simulator models exactly those three:

1. **Decode is memory-bandwidth bound.** Producing one token for one sequence
   streams the model's weights out of HBM; producing one token for sixty
   sequences streams them once. Step time is `weight_load + per_seq × batch`
   with a small `per_seq`. Batching is therefore nearly free, which is the
   entire reason continuous batching exists.
2. **Prefill is compute bound and blocks decode.** A long prompt is a large
   matmul; while it runs, nothing streams.
3. **KV cache is the binding memory constraint.** Batch size is limited by
   memory, not by configuration, and memory use grows one token at a time as
   generation proceeds — so admission has to be decided on a *prediction* of a
   length nobody knows.

Everything else — chunked prefill, paged attention, tensor parallelism,
speculative decoding — changes the constants and not the structure. What is
left out and why is in [`docs/known-limitations.md`](docs/known-limitations.md).

## The findings

| # | Finding |
|---|---|
| 3 | Capacity estimated the textbook way (peak decode throughput ÷ mean output length) **overstates the real figure by 27%**. A team sizing from it runs at a true utilisation of 1.00 believing they are at 0.79. |
| 4 | Tail TTFT rises **42×** between ρ=0.75 and ρ=0.95. TPOT barely moves. A dashboard tracking tokens per second shows this system getting *better* right up to the point users leave. |
| 6 | Prediction error is **nearly free when it decides order** (SJF's advantage is flat from a perfect oracle to 8× error) and **expensive when it decides memory** (attainment 91.4% → 75.6% from the same error). Use predictions for reversible decisions; avoid them for resource commitments. |
| 8 | Scheduling changes **who** waits, not **how much** waiting there is. DRR improves the interactive tenant's tail 2.7× and moves total throughput by −1%. |
| 9 | Doubling the fleet beats every shedding policy tested. **But the third replica is worth exactly 0.0 points** while the second is worth +33. The value of capacity is a property of where the purchase lands you on the curve, not of capacity. |
| 9b | The obvious clever admission policy — reject whatever will miss its deadline — is the **worst one measured**. It sheds 25% of traffic to reach 71.5% success; uniform random dropping does better while knowing nothing. |
| 10 | When KV memory binds, **deliberate over-subscription wins**: reserving 0.85× of projected need beats reserving 1.0×. When batch size binds, the same knob is pure loss. The two states look identical from a latency dashboard. |
| 11 | Little's Law holds to **within 0.08%** across every configuration — used here as an audit of the event loop, not as a talking point. It is what caught the worst bug in the simulator. |

## Running it

```powershell
# Windows: the wrapper sets up the MSVC environment and an offline CARGO_HOME.
.\cargo.ps1 test --offline --release      # 167 tests
.\cargo.ps1 run  --offline --release --bin run_gateway   # regenerates docs/results.md
.\demo.ps1                                # headline experiment, ~20 seconds
.\test.ps1                                # full six-stage verification
```

On a machine with a normal Rust install, `cargo` works directly.

Regenerating the report **rewrites `docs/results.md` in place**. That file is
checked byte-for-byte by `tests/results_integrity.rs`, so a stale report is a
failing test rather than a document nobody re-reads.

## Layout

| file | what it holds |
|---|---|
| `src/rng.rs` | xorshift64*, exponential and lognormal draws. `unit()` is strictly in (0,1) because `exponential` takes its logarithm. |
| `src/workload.rs` | Four request classes with genuinely different cost profiles and SLOs, two tenants with different mixes, and the `Estimator` — the only thing any policy is allowed to know about output length. |
| `src/engine.rs` | The accelerator model. `Running::charge()` and `Replica::can_admit` are the two subtlest pieces; both are pinned by tests named after the bug they had. |
| `src/sched.rs` | Five queue policies, five admission policies. `pop_admissible` is where policy lives; `pop_any` is an unordered drain hatch. |
| `src/sim.rs` | The event loop. Per-replica clocks, least-loaded fill, preemption with exponential backoff and a retry cap. |
| `src/metrics.rs` | Exact percentiles, TTFT/TPOT/goodput/slowdown, and `Little` — the self-audit. |
| `src/capacity.rs` | Measures saturation throughput empirically instead of deriving it. Section 3 is why. |
| `src/report.rs` | A prediction-registering document DSL. `render()` panics if a prediction was registered and never answered. |
| `src/experiments.rs` | The twelve experiments. Every number in the report is computed here at runtime; none is typed in. |

## The measurement discipline

Three rules, each of which exists because breaking it produced a wrong answer
during this build:

**Measure capacity, never derive it.** Every experiment is expressed as
ρ = λ/μ. The textbook μ is 27% too high, and a wrong denominator moves whole
sections into a different regime without any visible symptom.

**Register the prediction before running the experiment.** `Report::expect()`
takes the hypothesis; `Report::found()` takes the outcome and a boolean verdict;
`render()` refuses to produce a document with an unanswered question in it. Ten
of thirteen predictions here were wrong. That ratio is discussed in the report
rather than hidden — a majority of misses is a signal about the predictions, and
in two cases the misses were correlated, which is what a wrong mental model
looks like from the inside.

**Vary one channel at a time.** Section 6's first version varied "estimator
accuracy" and got a confounded answer, because the estimate feeds both the
scheduler and the memory manager. The giveaway was that FIFO's numbers moved,
and FIFO never consults the estimate for ordering. Separating the two channels
turned a muddy result into the report's most useful finding.

## Reading order

1. [`docs/results.md`](docs/results.md) — the experiments and their outcomes.
2. [`docs/portfolio/04-bugs-the-simulator-found.md`](docs/portfolio/04-bugs-the-simulator-found.md)
   — six real bugs, what each one looked like from the outside, and why five of
   them produced *plausible* output rather than a crash.
3. [`docs/known-limitations.md`](docs/known-limitations.md) — what this does not
   model and which conclusions therefore do not survive.
4. `docs/adr/` — the five decisions that shaped the model.
