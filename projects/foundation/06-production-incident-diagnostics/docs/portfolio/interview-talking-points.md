# Interview Talking Points

## Why this problem?
Troubleshooting engagements require more than recognizing terms like “N+1” or “stampede.” This project makes the observation, hypothesis, intervention, and verification loop executable.

## What is non-trivial?
The difficult part is keeping fault demonstrations real enough to measure while safe enough to run in a test suite. Every scenario has a budget, hard timeout, deterministic cleanup, and a metric whose direction should change.

## What fails and how does it recover?
The ten incidents cover persistence, pool/heap/worker saturation, downstream calls, retry amplification, queues, and cache concurrency. Fixed modes use targeted controls instead of a generic retry or restart.

## What are the trade-offs?
SQLite and in-process primitives make the lab portable. They cannot substitute for a production database, broker, network, or distributed cache; those limits are documented rather than hidden.

## How would it scale?
Export traces/metrics via OTLP, make scenario reports a time series, add a server-database profile, use a real broker/cache, and compare canary traffic partitions with production-safe capture limits.
