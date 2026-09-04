# Runbook — Ruleset Rollout

## Preconditions
The proposed rule change has a business owner, a uniquely incremented ruleset version, human-readable reasons for every outcome, typed condition review, and test coverage for boundaries. Existing versions are immutable and must never be overwritten.

## Simulate
1. Export/construct the candidate `RuleSetDefinition`.
2. Send it to `POST /api/v1/rulesets/what-if` with `loans:admin`.
3. Review total applications evaluated, decision flips, each application identifier, previous/candidate decisions, new required documents, rate adjustments, and conservative limit changes.
4. Investigate material flips with credit policy and ensure the candidate does not depend on unavailable facts.

## Publish
1. Publish the new version with `POST /api/v1/rulesets`; the API rejects a duplicate ID/version.
2. Create a new product version pointing to that ruleset version if policy applicability changes.
3. Apply the new product version only to new applications. Historical applications retain their bound product/ruleset versions and decision records.

## Rollback
Do not edit or delete the published version. Publish a new corrective version/product binding, document the rationale, run what-if again, and audit the rollout actor/correlation ID. Review any already-offered or accepted applications under the organization’s approved customer-treatment policy.
