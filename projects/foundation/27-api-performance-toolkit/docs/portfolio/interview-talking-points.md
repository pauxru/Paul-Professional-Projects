# Interview talking points

Use these when someone asks "walk me through this project".

## The 30-second summary

I built a load-testing toolkit in .NET 10 to fix three specific failures common in
homegrown load tools: closed-model harnesses that hide overload, missing coordinated-
omission handling that under-samples the slow tail, and naive comparisons that call any
delta a "regression". It's a CLI with a reusable core library, a bundled sample API
with six switchable pathologies, and a real measured before/after case study demonstrating
a `~3–5×` latency improvement with a Mann–Whitney U p-value under `0.0001`.

## The technical questions I want to be asked

### "Why did you build the load runner yourself instead of using k6?"

Two reasons. First, the practical one: the k6 binary is not installed on my build host,
and I wanted the project to `dotnet build && dotnet test` with zero external tooling.
Second, and more importantly, the portfolio angle is about *demonstrating* that I can
implement the correctness story — the open-model scheduler, coordinated-omission
handling, HdrHistogram-style buckets, and the statistical comparison. Delegating those
to a third-party tool would obscure the very thing I'm trying to show. ADR-001 is the
detailed write-up.

### "What is coordinated omission and how does your tool handle it?"

Coordinated omission is when a load harness waits for a slow response before starting the
next request — so the very slow responses that a user would experience are under-sampled.
My open model schedules arrivals at wall-clock intervals via a bounded channel; workers pull
from the channel and dispatch. Each request records two latencies: the *service latency*
(server-side duration) and the *intended-start latency* (time from the scheduler tick to
completion, i.e. includes queueing). Both are exposed as separate percentile series in
reports. In the case study, service p95 was 67 ms but intended p95 was 142 ms — the delta
is exactly what a naive closed-model harness would have hidden. See ADR-003.

### "Why Mann–Whitney U and bootstrap CI instead of a t-test?"

Latency distributions are heavily right-skewed — the mean is dominated by the p50 while the
tail is where users hurt. A t-test assumes normality and cares about the mean, so it's the
wrong tool. Mann–Whitney U is a non-parametric rank test — no distribution assumption —
and it tells you whether one sample tends to produce larger values than the other. Then I
add a bootstrap 95 % CI on the median difference because it gives an *effect-size interval*,
not just a "significant/not significant" binary. Both are unit-tested against identical
distributions (must not report a change) and against shifted distributions (must report the
change and get the sign right). See ADR-005.

### "How do you know the histogram is precise?"

Two ways. The layout is a straight port of the HdrHistogram algorithm — logarithmic bucket
index, sub-buckets for linear precision within each bucket, so relative error is bounded at
every scale. Then there's a unit test that fills 100 000 random values into the histogram
and asserts that reported p50 / p95 / p99 fall within the stated relative-error bound
against the exact answer. See `HistogramPrecisionBoundTests`.

### "How does the case study prove anything?"

The case study numbers are the output of actually running `loadrun` in Release mode against
the sample API in Release mode, twice — once with pathology headers on, once with them off.
The two `RunResult.json` files are checked in. The `compare` output is checked in. The
Mann–Whitney U p-value < 0.0001 and the bootstrap 95 % CI on the median difference is
`[−45.2, −34.4] ms` (entirely below zero) — so the improvement isn't noise. The write-up
in `docs/case-study-optimisation.md` includes the exact commands and the caveats about
loopback networking, single-host client/server contention, and SQLite.

### "What's the failure mode that would embarrass you in a code review?"

Two candidates:

1. **The HdrHistogram bucket layout.** During development I had a bug in `GetBucketIndex` for
   values below `subBucketCount` — bucket 0 needs special handling. That is now covered by a
   unit test that specifically records small values (0, 1, 2, `subBucketCount − 1`, `subBucketCount`)
   and verifies the roundtrip.
2. **The open-model scheduler cancellation.** The stub executor originally emitted spurious
   `Timeout` samples when the run deadline cancelled a pending `Task.Delay`. Now it re-throws
   `OperationCanceledException` and the scheduler catches it once, cleanly. The tests cover
   both "clean shutdown at deadline" and "the load model does not synthesise ghost errors".

### "What's on the future-work list?"

HTTP/2 and HTTP/3 opt-in per scenario, gRPC executor, live TUI dashboard, Prometheus
text-format export, and single-file distribution via `dotnet publish` per RID. Distributed
generation is possible but I'd only build it for a specific engagement — most APIs that
land on my desk don't need it.
