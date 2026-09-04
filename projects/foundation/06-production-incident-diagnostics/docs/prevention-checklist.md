# Prevention Checklist

Use this during design review, code review, and incident follow-up. A “yes” should be backed by code, test, query plan, or alert—not intention.

| Failure class | Review questions | Automated / operational prevention |
|---|---|---|
| INC-001 N+1 | Does collection traversal trigger queries in a loop? Is a projection or `Include` justified and bounded? | Interceptor-based commands/request regression test; trace EF spans. |
| INC-002 Index | Does every selective lookup have an explicit index and migration? Was the actual plan checked? | Migration review; representative `EXPLAIN` evidence; slow-query alert. |
| INC-003 Pool exhaustion | Are connections/transactions/readers disposed on all paths? Are acquisition timeouts finite? | Load test lease cleanup; pool wait/timeout metric and alert. |
| INC-004 Memory leak | Can static state, delegates, caches, event handlers, or timers retain request objects? | Retention test with forced GC; allocation and post-GC heap dashboards. |
| INC-005 Blocking async | Is `.Result`, `.Wait`, blocking I/O, or lock contention on a request path? | Analyzer/review ban; ThreadPool availability and queue-length alerts. |
| INC-006 Worker starvation | Is long/blocking work isolated behind bounded consumers? Are queue delays observed? | Bounded channel/scheduler test; queue age and work duration alert. |
| INC-007 Timeout | Does every outbound call have timeout and cancellation propagation? | Fault-injection timeout test; in-flight and timeout-ratio alert. |
| INC-008 Retry storm | Is retry ownership singular, budgeted, classified, jittered, and breaker-aware? | Downstream-call amplification test; retry/circuit telemetry. |
| INC-009 Poison queue | Is max delivery count configured with dead-letter/quarantine and replay ownership? | Poison-message test; delivery-attempt and DLQ depth alert. |
| INC-010 Cache stampede | Are concurrent cache misses coalesced or stale-while-revalidate? Is TTL jittered? | Origin-load-per-key test; cache miss / origin load ratio alert. |

## Cross-cutting review prompts
- Is every workload bounded by input validation, pagination, capacity, timeout, or cancellation?
- Does a failure surface a correlation ID and structured error rather than silently retry forever?
- Does the test assert a causal metric or merely that no exception occurred?
- Are retry, timeout, concurrency, and queue limits configurable and startup-validated?
- Is diagnostic data redacted and safe to capture under incident pressure?
- Does the runbook specify the abort condition and blast-radius limit for the diagnostic?
