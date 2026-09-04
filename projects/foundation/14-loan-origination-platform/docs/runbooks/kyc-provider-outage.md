# Runbook — KYC Provider Outage

## Trigger
Applications remain in `KycInProgress` and the decision trace/audit shows deterministic `provider-timeout` outcomes or a real provider timeout rate exceeds the service threshold.

## Immediate actions
1. Check `/health/ready`, API logs, correlation IDs, and the KYC adapter error classification.
2. Confirm no automatic retry exceeds the bounded two-attempt behavior; do not repeatedly resubmit the same application.
3. Keep applications in `KycInProgress`; do not bypass document or sanctions controls.
4. If permitted policy evidence exists, an authorized `loans:admin` user may use the manual override endpoint. Record the external evidence reference in the override reason; the system audits actor, reason, before/after hash, and correlation ID.

## Recovery
1. Restore the provider adapter or credentials outside this demonstration environment.
2. Re-run KYC only for timeout-classified applications, preserving the original audit history.
3. Reconcile outcomes: fail/sanctions decline, pass/refer moves to Screening, and manually overridden cases receive a post-incident review.
4. Monitor decision latency and referral counts after recovery.

## Escalation and prevention
Escalate prolonged outage to credit operations and security/vendor owners. In production add circuit-breaker dashboards, vendor status integration, a retry queue with jitter, and signed evidence attachment for overrides.
