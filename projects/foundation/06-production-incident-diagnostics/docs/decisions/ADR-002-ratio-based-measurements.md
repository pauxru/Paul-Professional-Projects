# ADR-002: Prefer ratios and categorical evidence to absolute thresholds

## Context
Timing varies with CPU scheduling, JIT warmup, antivirus, host load, and .NET ThreadPool heuristics. An assertion such as “p95 must be below 10 ms” would make a teaching lab flaky and could teach the wrong lesson.

## Options
1. Assert fixed absolute duration targets.
2. Record no timing data and test only code branches.
3. Measure timing, but assert structural ratios and categorical signals where possible.

## Decision
Record real elapsed time and percentiles in evidence, while tests prioritize ratios and categorical signals: SQL commands per order, `SCAN` versus indexed `SEARCH`, timeout count, retained object count, worker occupancy, queue-delay comparison, downstream call amplification, healthy queue throughput, and origin loads per cache miss.

## Consequences
Tests are robust across hosts and still catch meaningful regressions. Incident reports remain useful because they include actual local measurements and explain their context.

## Risks
Some timing improvements are too small to prove on a small SQLite dataset. The report must never imply a production SLA or generalized benchmark result.

## Alternatives
BenchmarkDotNet baselines could supplement this with controlled performance runs, but they would not replace scenario-level causal evidence.
