# Known limitations

What this simulator does not model, and which conclusions therefore do not
survive.

The engine model captures three things: decode is bandwidth bound so batching is
nearly free, prefill is compute bound so it blocks decode, and KV cache is the
binding memory constraint. Those three determine queueing behaviour. A good deal
else determines the constants, and none of it is here.

---

## Not modelled

### Chunked prefill

Prefill runs to completion for one request before decode resumes. Real
schedulers split a long prompt into chunks so it interleaves with decode steps
rather than blocking them wholesale.

**What this distorts.** Section 7's decode-priority case is more extreme than
any real system would be: 114 minutes of tail TTFT against 6.7 seconds. With
chunked prefill the two objectives genuinely trade against each other along a
curve. Without it, decode priority is a starvation failure rather than a
tuning choice. Section 7 says so explicitly and presents the case as a boundary,
not a proposal.

**What survives.** The direction. Prioritising decode does improve TPOT and does
harm TTFT, and the reason — prefill and decode contend for the same device — is
unchanged by chunking.

### Paged attention and fragmentation

KV cache is modelled as a single pool of tokens. Real allocators work in blocks
(typically 16 or 32 tokens) and suffer internal fragmentation, so usable
capacity is below nominal capacity by an amount that depends on the length
distribution.

**What this distorts.** Every absolute KV capacity figure in section 10. A
160,000-token pool here behaves like a somewhat larger real pool.

**What survives.** The shape of section 10, which is about the *relationship*
between the reservation factor and the binding constraint. Fragmentation
shifts where the constraint binds; it does not change that the optimum
reservation factor depends on which constraint binds.

### Tensor and pipeline parallelism

A replica is one indivisible unit. Sharding a model across devices changes the
constants and adds collective communication that this model has no
representation for.

**What this distorts.** Any mapping from "replica" to "GPU". A replica here is
whatever unit can independently run a batch.

**What survives.** Section 9's finding about the marginal replica, which is
about the utilisation curve rather than about hardware. If a second serving
unit halves utilisation, it does not matter how many devices that unit contains.

### Speculative decoding and prefix caching

Both change effective throughput substantially. Neither changes the queueing
structure — which is precisely why leaving them out is defensible for these
questions and indefensible for capacity planning.

**What this distorts.** Every requests-per-second figure. Prefix caching in
particular can eliminate most prefill work on a RAG workload with a shared
system prompt, which would move section 7 considerably.

**What survives.** Everything expressed as a function of ρ, because ρ is
measured against whatever μ the system actually has.

### Heterogeneous or degrading hardware

All replicas are identical and stay that way. Real fleets contain a slow node,
and the interesting failure is what a load balancer does when it finds one.

**What this distorts.** The least-loaded-first fill policy is optimal here
partly because replicas are interchangeable. With a slow node, "least loaded"
and "fastest to drain" diverge, and the naive policy sends *more* work to the
slow node precisely because it is falling behind.

### Request cancellation

Users close tabs. A gateway that keeps generating for a disconnected client is
wasting the scarcest resource it has.

**What this distorts.** Nothing in the report, because the workload has no
cancellations. It is listed because the fix — propagating cancellation into the
decode loop — is unglamorous, absent from most serving stacks, and worth more
than most scheduling work.

### Multi-turn conversations and session affinity

Every request is independent. Real chat traffic has sessions with retained
context, which creates both an affinity constraint (route to the replica holding
the prefix) and a correlation in arrival times.

**What this distorts.** The arrival process is Poisson, which is the friendliest
possible assumption. Correlated arrivals produce burstier queues at the same
mean rate, so every tail number here is optimistic.

---

## Methodological limits

### One workload

Four request classes with lognormal length distributions, two tenants, Poisson
arrivals. Real traffic is burstier, has diurnal structure, and contains
correlations this model has no way to express.

The classes are deliberately heterogeneous — chat and batch differ by more than
an order of magnitude in cost — because that heterogeneity is what makes
scheduling interesting. A homogeneous workload would make sections 5, 6 and 8
uninteresting for a reason that says nothing about real systems.

### One seed

Every experiment uses a fixed seed so the report is reproducible byte for byte.
That buys determinism at the cost of confidence intervals: the report does not
say how much of a 0.5-point difference is noise.

Where a difference is small enough for this to matter — the policy comparison in
section 5, where the spread is 2.50 to 2.59 — the report says so and leans on
the per-class breakdown instead, which moves by a factor of three and is not
plausibly noise.

### Slowdown as the primary metric

Latency is meaningless on a heterogeneous workload: a 4000-token generation
taking ninety seconds is behaving perfectly, a forty-token chat turn taking nine
is a disaster, and the first looks ten times worse in a latency histogram.

Slowdown — observed latency divided by the time the request would have taken
alone on the device — puts both on the same axis. It has its own failure mode:
it flatters a system that is uniformly slow, because a large ideal time makes a
large observed time look fine. The report quotes SLO attainment alongside it for
that reason.

### The estimator is synthetic

`Estimator` predicts output length from the request class with a stated
lognormal error. That is roughly what a production system can do — it knows the
endpoint and the prompt but not what the model will decide to write — but a real
predictor's errors are correlated with request content in ways this does not
capture. Section 6's conclusion is about the *structure* of prediction error
(which consumer it feeds), not about any achievable accuracy.

---

## What the report claims anyway

The conclusions that survive these omissions are the structural ones:

- Utilisation drives latency hyperbolically, so the same increment of load is
  free at one operating point and catastrophic at another.
- Scheduling redistributes delay; only admission control or capacity reduces it.
- Prediction error is cheap for ordering and expensive for resource commitment.
- The marginal replica's value depends entirely on where it lands you on the
  curve, not on how many replicas you already have.
- Measured capacity and estimated capacity differ by enough to move a system
  into a different regime.

The conclusions that do not survive are **any absolute number of requests per
second**. Every such figure in the report is an artifact of a synthetic engine
model, and its only job is to be the denominator of a ratio.

## Reproduction hazards on Windows

Two environment quirks bit hard enough during this build to be worth writing
down, because both of them fail *silently* in the direction of a green result.

### The argument separator must be quoted

`cargo.ps1` takes its arguments through `ValueFromRemainingArguments`. PowerShell's
own parameter binder consumes the first bare `--` in a command line as its
end-of-parameters marker, so it is stripped before the script ever runs:

```powershell
.\cargo.ps1 run --release --bin run_gateway -- --stdout    # WRONG: cargo sees --stdout
.\cargo.ps1 run --release --bin run_gateway '--' --stdout  # right
```

The failure mode is what makes this worth documenting. The determinism stage of
`test.ps1` originally used the unquoted form. Cargo exited non-zero, the stage
captured nothing, and it then hashed three empty strings -- which are of course
identical. The stage printed `sha e3b0c44298fc1c14 x3` and passed. That hash is
the SHA-256 of the empty input; it now reads as a fingerprint of a check that
verified nothing. A reproducibility test that cannot fail is worse than no test,
because it occupies the slot where a real one would go.

### `-Dwarnings` must be a single token

`cargo clippy -- -D warnings` is the documented invocation and it does not
survive the round trip through `cmd.exe` here: the two halves arrive separately
and `rustc` reads `warnings` as an input filename, reporting
`multiple input filenames provided`. `-Dwarnings` as one token works. This one at
least fails loudly.
