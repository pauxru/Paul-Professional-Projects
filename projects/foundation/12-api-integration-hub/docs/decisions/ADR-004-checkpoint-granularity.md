# ADR-004 — Checkpoint after each record

## Context

Large batches must resume after process interruption without replaying the entire source page. Checkpoints that are too coarse increase duplicate work; checkpoints that are too fine increase writes.

## Options

1. Checkpoint once per run.
2. Checkpoint once per fetched page.
3. Checkpoint after each successful or quarantined record.

## Decision

Use per-record checkpoints keyed by run, step, and batch. Advance only after the record is either durably loaded or durably quarantined.

## Consequences

Recovery starts at the first unfinished record and preserves poison-record isolation. SQLite performs more writes, which is acceptable for this demonstrator.

## Risks

Per-record persistence can limit throughput. Batching checkpoint updates would be necessary for very high-volume streams, paired with idempotent reprocessing of the small uncheckpointed window.

## Alternatives

Page-level checkpoints reduce database traffic but can replay an entire page. Run-level checkpoints do not meet the recovery objective.
