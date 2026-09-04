# Screenshots / Recordings Needed

Capture these to make the portfolio entry legible at a glance. Utilitarian styling is fine — the
point is the engineering. Use the fictional seeded data (`Acme Operations` workspace; Ada, Grace,
Linus).

## Must-have

1. **Two-tab live co-editing** — the headline.
   - Two browser windows side by side at `http://localhost:5021/`, signed in as
     `ada@acme.example` and `grace@acme.example`, both joined to the runbook.
   - Show the **same text** in both after concurrent typing, and **both cursors** visible in the
     presence panel. A short screen **recording (GIF/MP4)** is far better than a still here.

2. **Green build + test run** — credibility.
   - Terminal showing `dotnet build -c Release` (0 warnings/0 errors) and `dotnet test -c Release`
     (`55 passed, 0 failed`).

3. **The convergence property test** — the senior signal.
   - The test file / test name
     `RgaConvergencePropertyTests.RandomConcurrentEdits_FromMultipleClients_AlwaysConverge`
     and a snippet of `docs/conflict-resolution.md` §1.2 ("Why it converges").

## Strongly recommended

4. **Architecture diagram** — rendered Mermaid "realtime architecture (container view)" from
   `docs/architecture/architecture.md`.

5. **Operation-flow sequence diagram** — the rendered "operation flow with transform/merge" Mermaid.

6. **Reconnect/resync sequence diagram** — rendered from the architecture doc.

7. **History / diff view** — a `curl`/`Invoke-RestMethod` response for
   `GET /api/v1/documents/{id}/diff?from=..&to=..` showing insert/delete/equal segments, or the
   time-travel `at/{sequence}` response.

8. **Rate-limit disconnect** — log/console output from the abusive-client integration test, or a
   captured `Rejected{ code = "rate_limited" }` event.

## Nice to have

9. **Presence lifecycle** — the rendered state diagram (join → active → idle → evicted).

10. **OpenTelemetry metrics** — console exporter output showing `collab.clients.connected`,
    `collab.operations.applied`, `collab.apply.duration`, `collab.resync.count`,
    `collab.operations.rejected`.

11. **ER diagram** — rendered Mermaid from `docs/database-schema.md`.

## Capture notes

- Prefer **short recordings** for anything realtime (co-editing, cursors, resync); stills undersell it.
- Keep secrets out of frame (there are none real, but avoid showing any `.env`).
- Label everything as fictional demo data where a viewer might assume otherwise.
