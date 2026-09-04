# Claims Cutover Runbook

## Roles and authority
| Role | Responsibility |
|---|---|
| Cutover lead | Coordinates timeline, records decisions, owns go/no-go |
| Migration operator | Runs backup, import, reconciliation, and evidence capture |
| Claims business validator | Confirms workflow and sampled financial values |
| Security owner | Confirms legacy exposure mitigation and access policy |
| Platform operator | Changes facade routing and monitors health/telemetry |

## Pre-checks (T-5 to T-1)
- [ ] Approved change record, communications, named rollback authority, and maintenance window.
- [ ] Modern Release build/test evidence is green.
- [ ] Target migration has run successfully against a copy of source data.
- [ ] `GET /health/live` and `/health/ready` are healthy on the intended target.
- [ ] Token/policy tests prove `claims:read`, `claims:adjust`, and `claims:approve` access boundaries.
- [ ] Storage capacity, document manifest, backup location, and restore procedure are verified.
- [ ] Facade rules are reviewed, with a tested switch back to legacy route.
- [ ] Source/target connection strings and production signing key are managed outside source control.

## Freeze and migrate
1. Announce claims-write freeze and show maintenance banner.
2. Confirm no active intake/assessment transactions; capture current timestamp and legacy DB/file backup checksum.
3. Make legacy claims write access read-only at the facade/application layer.
4. Apply target migration; verify migration history and target backup point.
5. Run `LegacyClaimImporter` against final source snapshot.
6. Persist the validation report: source/imported/rejected counts, reference checksums, status counts, monetary totals, and rejected rows.
7. Stop immediately if any rejected record lacks an approved remediation decision.

## Verify
- [ ] Imported + rejected equals source count.
- [ ] Zero unexplained reference differences.
- [ ] Claimed/reserve totals by currency match accepted source rows.
- [ ] Random sample includes Submitted, UnderReview, Approved, Rejected, Settled, Closed where available.
- [ ] Business validator signs off settlement samples against the characterized calculator.
- [ ] Document manifest checks pass or documented exceptions are accepted.
- [ ] Modern health, OpenAPI, authentication, authorisation, and stale-version smoke checks pass.

## Switch
1. Enable modern `/api/v1/claims` routes in the facade for the defined ownership slice.
2. Keep legacy route available but read-only for the rollback window.
3. Send a synthetic authenticated list, intake, assessment, approval, settlement, and document request.
4. Monitor 4xx/5xx/429 rate, correlation IDs, and database errors for the agreed hypercare period.
5. Communicate successful switch and clearly label remaining legacy functions.

## Rollback triggers
Rollback if any of the following occurs:
- Reconciliation has unexplained count, reference, attachment, or monetary differences.
- A critical workflow is blocked or a valid settlement differs from characterized behavior.
- Authentication/policy boundary failure, unhandled 5xx pattern, or data corruption is detected.
- Target health/readiness becomes unhealthy beyond the agreed operational threshold.

## Rollback procedure
1. Stop new target writes and record exact timestamp/correlation IDs.
2. Switch facade claims ownership back to legacy read/write path.
3. Restore legacy access from pre-freeze backup only if legacy was modified; otherwise retain it unchanged.
4. Preserve modern database/logs/import reports for diagnosis; do not delete evidence.
5. Reconcile claims created during the attempted window before any retry.
6. Conduct an incident review, correct the root cause, dry-run again, and obtain fresh go/no-go approval.
