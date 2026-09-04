# Interview Talking Points

1. **Business fit:** lending policy needs reasons, not a fake model score; every outcome has a version and human-readable trace.
2. **Architecture:** I chose a modular monolith because transactional workflow consistency and local reproducibility outweigh distribution for this scope.
3. **Hard failure paths:** provider timeout remains in KYC after bounded retry; failed/pending/reversed payments retain an audit/reconciliation record.
4. **Financial correctness:** `decimal` calculations, minor-unit rounding, and final installment adjustment are directly tested rather than assumed.
5. **Security:** scope policies, privileged override audit, document constraints, no real PII, production signing-key guard, and explicit residual risks.
6. **Scale path:** keep ports, move SQLite/files to managed DB/blob, replace local adapters, use OIDC/webhooks/outbox, and externally anchor audits.
7. **Trade-off:** aggregate JSON snapshots prioritize historical decision reconstruction; production analytics and retention require richer projections and governance.
