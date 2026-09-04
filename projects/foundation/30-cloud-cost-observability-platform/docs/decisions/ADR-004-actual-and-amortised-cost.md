# ADR-004: Persist actual and amortised cost side by side

## Context
Cash invoice cost, reservation amortisation, credits, and discounts answer different questions. A single cost column loses the distinction.

## Options
1. Persist only invoice/actual cost.
2. Persist only amortised cost.
3. Persist both and require an explicit reporting basis.

## Decision
Each cost record stores actual cost, amortised cost, credits, discounts, reserved coverage, and reserved utilisation. Query and allocation APIs default to amortised cost but accept actual cost explicitly.

## Consequences
Showback can align with economic consumption while invoice reconciliation stays possible. Consumers must label their selected basis.

## Risks
Provider fields are not identical across Azure and AWS. Adapter mapping documentation makes assumptions visible.

## Alternatives
Normalising away provider charge types at ingest was rejected because it makes audit/reconciliation harder.
