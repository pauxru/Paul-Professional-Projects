# SRE Practices Guide

## SLO adoption guide
Start with one customer-visible journey per service, an explicit eligible-event denominator, an owner, and a review cadence. Choose request-based SLIs when traffic weighting represents harm; choose window-based SLIs when an entirely bad minute is materially meaningful. Baseline behavior before setting a target, then review false positives and product expectations quarterly.

## Alert quality
Page on actionable burn, not every error. Pair a long confirmation window with a short confirmation window, deduplicate alerts under a declared incident, and record `DetectedAt`, source-data lag, acknowledgements, and recoveries. Review alert-to-incident ratio, false-positive rate, and flapping alerts. A quiet on-call that misses harm is not healthy; neither is a noisy on-call that trains people to ignore pages.

## On-call hygiene
Maintain current rotation ownership, a service catalogue, tested access paths, and concise runbooks. Acknowledge quickly, state a commander and communications lead for material events, log observations with timestamps, and avoid making risky unrelated changes during an active response. Conduct a handoff for unresolved incidents and track follow-up action items to closure.

## Blameless postmortem culture
Write what happened, the measured impact, contributing conditions, what went well, and what made detection or recovery harder. Do not replace mechanism with individual blame. The goal is to improve systems, decision context, guardrails, and learning loops. Every action has an owner and due date; overdue actions are reportable work, not invisible debt.

## Toil budgeting
Treat repetitive manual operational work as a reliability signal. Reserve capacity for runbook automation, noisy-alert removal, dependency hardening, and recurring factor remediation. The deploy gate deliberately turns low error-budget headroom into a visible trade-off: feature risk needs explicit review before it borrows from resilience work.
