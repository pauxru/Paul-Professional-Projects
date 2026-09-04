# Data Migration Design

## Source and target schema diff
| Concern | Legacy schema | Modern schema | Migration treatment |
|---|---|---|---|
| Policyholder | Integer ID, name/email | GUID ID, unique email | Generate GUID; normalize email; reject missing required identity |
| Policy | Integer ID, policy number, decimal fields | GUID ID, unique policy number, checks | Map by policy number; preserve currency/deductible/limit |
| Claim | Denormalized policyholder name and duplicated deductible/limit | GUID aggregate references policy; status enum; version | Map policy via ACL, preserve claim/reference/amount/reserve/status |
| Settlement | Computed ad hoc, not persisted separately | `SettlementAmount` is explicit | Backfill zero until a verified historical settlement source exists |
| Status | Free-form text | Enumerated state | Parse known values; reject unknown values with reason |
| Document | Local file path and metadata | Storage key + metadata | Generate a manifest, copy/checksum files, then write storage keys |
| Concurrency | None | `Version` token | Initialize imported claims at version 1 |

## Field mapping
| Legacy field | Modern field | Rule |
|---|---|---|
| `Claims.Id` | Import report `LegacyClaimId` only | Do not expose as target identity |
| `Claims.ClaimReference` | `Claims.Reference` | Trim/uppercase; unique constraint; reject duplicate |
| `Claims.PolicyId` + `Policies.PolicyNumber` | `Claims.PolicyId` | Resolve/create policy through anti-corruption layer |
| `Policyholders.Name/Email` | `Policyholders.Name/Email` | Preserve synthetic values; target email is unique |
| `Claims.ClaimedAmount` | `Claims.ClaimedAmount` | Must be > 0 |
| `Claims.ReserveAmount` | `Claims.ReserveAmount` | Clamp only negative legacy reserve to zero; record mapping decision |
| `Policies.Deductible/PolicyLimit` | policy monetary fields | Must meet target database checks |
| `Claims.Currency` | `Claims.Currency` | Validate three-letter code and policy match |
| `Claims.Status` | `Claims.Status` | Case-insensitive enum parse; reject unsupported values |
| `Claims.Adjuster` | `Claims.AssignedAdjuster` | Preserve null/trim semantics |
| `Claims.CreatedUtc` | `Claims.CreatedAt` | Parse ISO timestamp; fallback needs rejection in production migration |

## Import implementation
`LegacySqliteClaimSource` is the anti-corruption adapter: it joins the legacy tables and emits neutral `LegacyClaimRow` records. `LegacyClaimImporter` owns validation, target aggregate creation, duplicate detection, policy creation, source checksum, imported-reference checksum, and rejected-row reporting. This prevents raw legacy table names and permissive strings leaking into the modern domain.

The included test seeds a real legacy SQLite schema, injects one negative-amount row, then proves: source count is three, valid imports are two, the invalid record is rejected with a reason, and mapped claim fields survive.

## Backfill approach
1. Stop schema-changing legacy releases.
2. Create a read-only database copy and document manifest.
3. Run a dry import to a disposable target database.
4. Resolve rejected rows; do not silently default business values.
5. Take a final backup, freeze claims writes, run a delta import, and produce report artifacts.
6. Validate, switch routes, and retain source backup for the agreed rollback window.

## Reconciliation checks
| Check | Expected result | Owner |
|---|---|---|
| Source row count vs imported + rejected | Exact equality | Migration operator |
| Claim-reference set | Deterministic checksum / explicit difference list | Data steward |
| Count by status | Equal after accepted/rejected accounting | Claims operations |
| Claimed/reserve totals by currency | Equal for accepted rows | Finance/claims reviewer |
| Policy number mapping | Every imported claim has one target policy | Data steward |
| Attachment manifest | File count, byte size, SHA-256, missing-file list | Operations |
| Sampling | Field-level sample across statuses and high values | Business approver |

## Data migration validation report format
```text
Run ID: 2026-09-03T00:00:00Z-example
Source rows: 3
Imported rows: 2
Rejected rows: 1
Source checksum: <SHA-256 of ordered source identity/value rows>
Imported checksum: <SHA-256 of ordered imported references>
Rejected: CLM-BAD-001 — Claimed amount must be greater than zero.
```
Checksums are evidence aids, not a substitute for row-level investigation; source and target checksum inputs intentionally differ because target IDs are regenerated.
