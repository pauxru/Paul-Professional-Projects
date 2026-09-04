# Runbook: Budget Breach

## Trigger
The budget status endpoint reports a newly crossed 50%, 80%, 100%, or 120% threshold.

## Procedure
1. Confirm period, selector, currency, and cost basis.
2. Use service/team/environment breakdown and compare the forecast band's upper bound to remaining budget.
3. Confirm whether the crossing is caused by an approved migration, commitment amortisation, or late cost restatement.
4. Notify accountable owner with spend, forecast, leading resources, and allocation audit link.
5. At 100%/120%, agree whether to contain spend, raise budget through governance, or accept risk; document the decision.

## Alert hygiene
The system stores previously crossed thresholds, so a repeated status poll does not create another alert. Do not reset alert state to force notification; create a new budget period or use a separate operational incident.
