# FinOps Practices in This Reference Implementation

## Inform
FinOps starts with trusted visibility. The platform keeps resource inventory, provider-style meters, usage, actual/amortised cost, credits, discounts, commitment coverage, FX rates, tag coverage, and allocation audit lines. A team should be able to answer both “what did we spend?” and “why was this assigned to us?” without relying on a dashboard total alone.

## Optimise
Recommendations have evidence, a quantified monthly projection, confidence, and explicit remediation steps. The generator demonstrates idle compute, orphaned resources, oversized SKUs, non-production scheduling, storage tiering, commitments, and untagged resources. The implementation deliberately models `Open → Accepted → Implemented → Verified | Dismissed`; projected savings become realised savings only after actual post-change cost is compared with captured baseline.

## Operate
Budgets use persisted 50/80/100/120% crossings to avoid re-alert spam. Anomalies are investigated through observed/expected values, contributing dimensions, groups, acknowledgment/suppression, and runbooks. Cost allocation rules are a controlled operating policy: their order is reviewed, and reports keep the residual visible.

## Showback versus chargeback
**Showback** informs teams of the cost they influence. **Chargeback** uses an agreed allocation policy to transfer accountability or internal billing. Both require the same integrity property: no hidden cost and no unexplained split. This project supports both as allocation reports; it does not implement an accounts-payable ledger or internal invoice issuance.

## Unit economics
Spend only becomes operationally useful when joined to a business denominator. The local business-metrics feed supplies fictional orders, active tenants, and GB processed by team/day. The unit-economics engine produces cost/order, cost/active tenant, and cost/GB, allowing teams to distinguish rising cloud spend that accompanies healthy business volume from worsening efficiency.

## Engineering incentives
Teams should receive prompt, non-punitive visibility; owner/application tags should be part of delivery definitions of done; shared platform costs should use a published split rationale; and verified savings should be celebrated more than projections. An allocation system that looks exact but silently drops untagged spend creates bad incentives, which is why this implementation retains `unallocated` explicitly.
