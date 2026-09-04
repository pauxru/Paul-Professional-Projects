# Cost and Sizing Notes

These are **rough order-of-magnitude estimates, not quotes**. Azure prices vary by region, agreement, currency, utilization, reservations, egress and service evolution. Recalculate with the Azure Pricing Calculator before approval.

| Environment | Approximate monthly range (USD) | Main drivers |
|---|---:|---|
| Dev | $150–$450 | Burstable PostgreSQL, Basic Redis/ACR, Standard Service Bus, low Container Apps usage/log ingestion |
| Staging | $500–$1,500 | General-purpose PostgreSQL, Standard Redis, private endpoints, representative replica uptime/logs |
| Prod | $2,000–$7,000+ | Zone-redundant PostgreSQL, Premium Redis/Service Bus/ACR, three minimum replicas, 90-day telemetry, backup and egress |

## Sizing method

1. Measure request concurrency, p95/p99 latency and CPU/memory per revision.
2. Measure DB CPU, storage IOPS, connection count, query latency and backup window.
3. Measure Redis hit rate, memory headroom and eviction.
4. Measure Service Bus ingress, active/dead-letter depth and processing latency.
5. Measure telemetry GB/day; sampling and retention often dominate unexpected cost.
6. Set budgets and anomaly alerts before production.

## Cost controls

- Scale dev to zero where operationally acceptable.
- Use low dev retention and sampling.
- Separate noisy diagnostics from required audit/operational data.
- Review private endpoint, egress and cross-region charges.
- Reserve PostgreSQL/Redis capacity only after a stable baseline.
- Tag every resource and allocate shared costs explicitly.
