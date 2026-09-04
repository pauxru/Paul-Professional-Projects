# ADR-003: Bounded Subset-Sum for Many-to-One

## Status

Accepted, dated 2026-09.

## Context

Settlement feeds can aggregate several internal transactions into one external settlement line, or split one internal transaction across several external lines. ReconEngine models these as `ManyToOneMatchRule` with `RuleId` `many-to-one-subset-sum` and `OneToManyMatchRule` with `RuleId` `one-to-many-subset-sum`.

General subset-sum is exponential. The code therefore uses `SubsetSum.Pool` and `SubsetSum.Find` with caps from `MatchingRuleSetDefinition`: `SubsetSumMaxGroupSize = 4`, `SubsetSumMaxCandidates = 20`, and `SubsetSumDateWindowDays = 3`. Candidate pools are restricted to positive amounts, same currency, and value dates within the configured window, then deterministically ordered by date distance, amount, and `RowHash`.

## Decision

Support many-to-one and one-to-many matching through bounded subset-sum search. The search is enabled and tuned by the ruleset definition, but its worst-case work is bounded by explicit caps: at most 20 candidates, groups of at most 4 records, and a 3-day date window by default. It uses the existing amount tolerance (`AmountToleranceMinor`) and returns auditable `MatchCandidate` explanations describing the group size and date window.

## Options Considered

1. Bounded subset-sum with candidate and group caps.
   - Pros: finds common batched settlements and split payouts; deterministic; protects the hot path from unbounded exponential work; auditable through rule IDs and predicates.
   - Cons: may miss legitimate groups larger than 4 or outside the 20-candidate pool; result quality depends on deterministic pool ordering.
2. Exact exhaustive subset-sum over every possible candidate.
   - Pros: more complete for arbitrary group sizes; fewer missed large groupings.
   - Cons: exponential and unsafe for production-sized files; difficult to bound in the same pipeline that handles 250,000 pairs / 500,000 rows.
3. Skip grouped matching entirely.
   - Pros: simpler and faster; fewer false positives.
   - Cons: common settlement batching would become manual exceptions; lower auto-match rate; less useful fee and refund reconciliation.
4. Use only reference-based grouping.
   - Pros: faster and more explainable when references are reliable.
   - Cons: misses PSP batches where references are absent, transformed, or shared; does not solve amount-only split settlement cases.

## Consequences

Positive consequences:

- The engine can auto-match realistic batched payouts and split settlement lines.
- Worst-case complexity is bounded by `SubsetSumMaxCandidates` and `SubsetSumMaxGroupSize` rather than unbounded file size.
- Date and currency filters reduce false positives and search cost.
- Each grouped match records a concrete rule ID and explanation.

Negative consequences:

- Some valid large groupings will be left unmatched and classified as exceptions.
- Candidate ordering can affect which valid subset is found when multiple subsets satisfy the target.
- The rule adds more configuration that must be tested when rulesets are versioned.

## Risks

- Risk: increasing caps in a ruleset causes a combinatorial performance regression. Mitigation: treat caps as guarded operational parameters, benchmark changes, and keep defaults at 4 records and 20 candidates.
- Risk: false positives when different same-currency transactions share date and amount characteristics. Mitigation: constrain by date window, positive amounts, tolerance, and deterministic ordering; expose lower confidence (`0.8`) than exact matching.
- Risk: missed large batches create manual work. Mitigation: surface unmatched exceptions and tune profiles or add future batch-reference rules if real feeds justify them.

## Alternatives

A reviewer might expect an optimal dynamic-programming subset-sum implementation. That was not chosen because transaction amounts are `long` minor units and settlement amounts may be large, making amount-indexed DP memory-sensitive. The bounded combinational search aligns better with the engine's auditability and predictable hot-path performance goals, accepting incomplete coverage for very large groupings.
