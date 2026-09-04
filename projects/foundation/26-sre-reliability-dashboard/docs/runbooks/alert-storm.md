# Runbook: Alert Storm

## Trigger
Many firing alerts, alert-to-incident ratio degradation, or repeated state transitions indicate an alert storm.

## Immediate actions
1. Declare one coordinating incident if broad customer impact is credible.
2. Verify alert source-data lag and rule windows before treating alerts as independent incidents.
3. Apply a declared maintenance window only for planned work; use open-incident suppression for duplicate affected-service alerts.
4. Preserve a representative sample of alerts, timestamps, and links for alert-quality review.

## Triage
1. Group alerts by service, dependency, rule, and first firing time.
2. Identify common upstream services using the catalogue dependency graph.
3. Prefer a source fix over muting. Manual suppression requires a reason and remains visible.
4. Watch for flapping: three or more state transitions in a sequence merits remediation.

## Follow-up
Link meaningful alerts to the incident; classify resolved unlinked alerts during review; tune routing or SLI filters only after validating the hand-computable burn semantics remain intact.
