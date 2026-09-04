# ADR-002: Use strangler fig rather than big-bang replacement

## Context
Claims operations contain workflow and settlement behavior that may be implicit in legacy code. A full replacement creates a long validation gap and makes rollback difficult.

## Options
1. Rewrite all claims screens and data in one release.
2. Maintain dual-write synchronization between legacy and modern applications.
3. Route bounded slices through a facade, with explicit ownership and an importer.

## Decision
Choose option 3. Move list/read first, then new intake and assessment, then approval/settlement/documents under a controlled freeze. Keep one writer per claim reference.

## Consequences
Migration produces useful value and risk reduction earlier. The project must maintain route ownership, reconciliation evidence, and an operational rollback switch.

## Risks
Coexistence lengthens operational complexity, and facade behavior must be tested in the eventual deployment environment.

## Alternatives
Big bang makes verification and rollback too coarse. Dual-write makes distributed failure/reconciliation a core problem before it is necessary.
