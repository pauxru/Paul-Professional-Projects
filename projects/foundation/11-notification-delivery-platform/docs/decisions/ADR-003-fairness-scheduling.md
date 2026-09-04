# ADR-003 — Fairness scheduling per tenant

## Context

In a multi-tenant delivery platform, a noisy tenant will always exist. If
the pipeline dequeues in FIFO order the noisy tenant's 10,000 marketing
messages will delay another tenant's 5 password resets. That is
unacceptable.

## Options

1. **FIFO.**
2. **Per-tenant queues + strict round robin.**
3. **Weighted round robin.**
4. **Priority + token bucket per tenant.**

## Decision

Adopt **option 3**: `TenantFairnessScheduler.PickBatch` returns a batch
composed of at most K rows per tenant per round, with tenants ordered by a
weight derived from tier and outstanding queue depth. Priority is preserved
inside a tenant — `Transactional` sorts ahead of `Marketing`.

## Consequences

- Small tenants are not starved by noisy tenants.
- The fairness property is testable directly at unit level
  (`FairnessSchedulerTests`) and end-to-end at pipeline level
  (`DeliverySuccessTests.PipelineProcessesBothTenantsFairly`).
- Priority is respected inside a tenant.

## Risks

- Weight function is heuristic and easily misconfigured. Defaults are
  documented on `NotificationOptions`.
- With extremely lopsided tenants (10^5 vs 1) even a fair scheduler will
  need many rounds to drain — that's inherent to the workload, not a bug.

## Alternatives revisited

If we ever expose a "priority tenant" tier, this design accommodates it
without an interface change: raise the weight and lower the cap.
