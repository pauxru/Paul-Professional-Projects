# ADR-004: Use an anti-corruption layer and importer rather than dual-write

## Context
Legacy claims use integer identifiers, denormalized holder names, free-form status text, and duplicated policy values. Directly exposing this schema to the modern domain would perpetuate its terminology and weak invariants.

## Options
1. Share the legacy schema from the new API.
2. Dual-write each claims mutation to both schemas.
3. Translate source rows through an anti-corruption layer and import with validation/reporting.

## Decision
Choose option 3. `LegacySqliteClaimSource` reads the source schema; `LegacyClaimImporter` maps neutral rows into target aggregates and returns counts, checksums, and row-level rejections.

## Consequences
The domain remains independent of legacy table naming and integer identity. Cutover requires a freeze/delta import rather than continuous bidirectional synchronization.

## Risks
Import mapping must be maintained as discovery finds additional data variants. Attachment migration requires a separate manifest process.

## Alternatives
Shared schema blocks modernization boundaries. Dual-write introduces two failure domains and ambiguous source-of-truth semantics.
