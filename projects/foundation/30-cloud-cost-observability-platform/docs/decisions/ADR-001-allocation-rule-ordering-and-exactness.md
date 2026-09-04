# ADR-001: Ordered allocation rules with an explicit exactness invariant

## Context
Cloud resource tags are incomplete, shared services cannot always carry a team tag, and finance reports must reconcile to provider cost.

## Options
1. Use a single tag lookup and discard unmatched cost.
2. Allocate with independent unordered heuristics.
3. Use an ordered rule chain with an explicit residual bucket.

## Decision
Use direct tag, resource-group/subscription mapping, parent inheritance, then shared proportional/even/fixed splitting in configured order. Any remaining amount becomes `unallocated`. For each source record, `allocated + unallocated == source` is asserted with `decimal` arithmetic.

## Consequences
Every dollar remains visible and the audit line explains its rule. Rule ordering is a business control and must be reviewed when changed.

## Risks
Bad mappings can confidently misallocate spend. The audit trail helps detect this but does not replace ownership review.

## Alternatives
Machine-learned ownership inference was rejected because it is difficult to explain and cannot be the only basis for chargeback.
