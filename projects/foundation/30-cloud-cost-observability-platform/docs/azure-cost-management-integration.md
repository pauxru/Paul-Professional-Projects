# Azure Cost Management Integration Design

## Explicit boundary
**Nothing was provisioned or called.** This repository does not authenticate to Azure, create exports, query Azure APIs, or require an Azure subscription. This document describes the adapter boundary intended for a real deployment.

## Cost Management exports → `ICostDataSource`
The implemented `AzureCostManagementCsvDataSource` streams one CSV row at a time and maps the export-shaped columns below to `CostImportLine`.

| Azure Cost Management export column | Neutral field | Notes |
|---|---|---|
| `Date` / `UsageDate` | `UsageDate` | Parsed as `DateOnly` |
| `BillingPeriodStartDate` | `BillingPeriod` | Normalised to `yyyy-MM` |
| `ResourceId` | `ResourceId` | Must exist in inventory or is rejected for recovery |
| `MeterName` | `Meter` | Part of idempotency identity |
| `ServiceName` / `ConsumedService` | `Service` | Categorised into compute/storage/etc. |
| `Quantity`, `UnitOfMeasure` | usage quantity/unit | Consumption dimensions |
| `EffectivePrice` | rate | Falls back to cost/quantity |
| `CostInBillingCurrency` | actual cost | Invoice/cash basis |
| `AmortizedCost` | amortised cost | Economic-consumption basis |
| `Credit`, `Discount` | credits/discounts | Kept separately |
| `ReservationId`, reservation utilisation | coverage/utilisation | A populated reservation ID indicates covered usage |
| `BillingCurrency` | currency | FX table converts reporting totals |

In production, scheduled Cost Management exports would land in a least-privilege Storage Account container and an adapter would stream blob contents with managed identity. Blob name, export run ID, invoice section, and provider line item identity would be retained as provenance.

## Cost Details API
For incremental or drill-down data, an Azure implementation would call Cost Management **Cost Details** with a managed identity and bounded date/resource scopes. The response mapper would populate the same neutral row fields and preserve the raw provider key for reconciliation. Retry/backoff, pagination, schema versioning, throttling handling, and an immutable raw landing copy are required before production use.

## Advisor recommendations
Azure Advisor recommendations are complementary evidence, not a source of financial truth. A production `IRecommendationEvidenceSource` adapter would map Advisor resource ID, category, impact, recommendation ID, and remediation text to a recommendation evidence attachment. The local recommendation engine retains its own quantified projection and lifecycle; it would never auto-implement Advisor actions.

## Resource Graph inventory
An Azure Resource Graph adapter would query resource ID, type, location, subscription ID, resource group, SKU, tags, created/deleted signals where available, and parent relationships inferred from resource IDs/references. It would upsert inventory before cost import so cost rows with unknown resources become recoverable exception work rather than silent unallocated cost.

## Azure Monitor metrics
Azure Monitor metrics would feed utilisation evidence: VM CPU/memory, storage transactions, database utilisation, function executions, and network/load-balancer activity. Aggregated daily metrics should be stored with source timestamp/window, then passed to the recommendation engine for idle, rightsizing, schedule, storage-tier, and orphan rules. Metrics need their own delay/restate logic because monitoring windows can arrive late.

## Identity, access, and data controls
Use managed identity with the smallest roles required (Cost Management Reader, Reader/Resource Graph access, Monitoring Reader, storage read for exports), scope identities per environment, store no client secret in source control, and restrict raw export access because costs are competitive intelligence. Export/download actions should be audited and team-scoped reporting must not infer authorization from user-controlled tags alone.

## AWS parity
The adjacent AWS CUR-like CSV adapter maps CUR `lineItem/*`, `product/*`, `pricing/*`, `reservation/*`, and `savingsPlan/*` fields into the same neutral shape. That is why allocation, forecasting, governance, and reports do not know which provider supplied a row.
