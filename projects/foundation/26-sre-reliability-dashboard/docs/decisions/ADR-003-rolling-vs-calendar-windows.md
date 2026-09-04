# ADR-003: Preserve Rolling and Calendar SLO Windows

## Context
Calendar windows reset on a human reporting boundary; rolling windows continuously include the previous duration. Treating them as interchangeable causes inaccurate budget and trend statements around month rollover.

## Options
1. Implement calendar windows only.
2. Implement rolling windows only.
3. Model window kind explicitly and resolve both in the domain.

## Decision
Choose option 3. Monthly and quarterly calendar windows start at UTC boundaries; rolling windows use exact durations ending at the evaluation instant.

## Consequences
Results include resolved start, end, calendar end, and compliance duration. Tests use a fake clock across February and quarterly boundaries.

## Risks
UTC may not match a business’s contractual locale. A production policy would declare the approved timezone and holiday/reporting conventions.

## Alternatives
Fixed custom windows are a plausible extension but are not added without a documented contract and reset semantics.
