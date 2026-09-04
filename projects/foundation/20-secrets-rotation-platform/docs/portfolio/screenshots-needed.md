# Screenshots Needed

Capture only synthetic data. Check every frame for tokens, environment values, terminal history,
local paths, and generated secret values before publishing.

1. Dashboard overview with inventory, rotation progress, expiry, and anomaly cards.
2. A dual-write rotation paused at `AwaitingAcknowledgement`.
3. The same rotation at `Completed` with acknowledgement status visible.
4. A verification-failure rotation at `RolledBack`.
5. OpenAPI document showing grouped `/api/v1` surfaces.
6. Test terminal showing the final passing Release summary.
7. Architecture Mermaid rendering.
8. Envelope-encryption Mermaid rendering.
9. Security review threat table.
10. Consumer SDK sample code with no value printed.

Recommended crop: project UI and command output only. Do not show unrelated desktop content.
