# Architecture overview

See `README.md` § *Architecture* and § *Architecture Diagram* for the container view, the
appointment-lifecycle state machine, the slot-availability computation flow, and the
break-glass access sequence.

Additional context is spread across:

- `docs/scheduling-model.md` — algorithmic details of slot generation and concurrency.
- `docs/database-schema.md` — table-by-table breakdown and ER Mermaid.
- `docs/security/security-review.md` — STRIDE and control coverage.
- `docs/privacy-considerations.md` — data-protection design considerations.
- `docs/decisions/` — six ADRs covering the significant design choices.
