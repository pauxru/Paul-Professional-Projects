# Demo Script — Collab

A tight, ~6-minute walkthrough that shows the engineering, not just the UI. Fictional context:
*Acme Manufacturing (fictional)* operations team co-editing an incident runbook.

## 0. One-command demo (optional)

```powershell
./scripts/demo.ps1
```

Starts the API on `http://localhost:5021`, drives the REST surface with the two seeded users, then
points you to the browser for live co-editing. Everything below can also be done by hand.

## 1. Build & test in front of them (60s) — credibility first

```powershell
dotnet build -c Release      # 0 warnings, 0 errors
dotnet test  -c Release      # 55 passed, 0 failed (30 unit + 25 integration), no infra
```

Say: *"No Docker, no database server — SQLite and an in-memory SignalR test host. It just runs."*

## 2. The two-tab live edit (90s) — the wow, kept honest

- Run the API, open **two browser tabs** at `http://localhost:5021/`.
- Tab 1: sign in `ada@acme.example`; Tab 2: sign in `grace@acme.example`. Both **Connect & join** the
  seeded runbook `55555555-5555-5555-5555-555555555555`.
- Type in both at once at the **same cursor position**. Point out: edits converge to the **same text
  on both tabs**, and you can see the other cursor.
- Be honest: *"This is a character-level CRDT. Concurrent typing can interleave — I don't pretend it's
  semantic merge. That trade-off is documented."*

## 3. Why it converges (120s) — the real content

Open `docs/conflict-resolution.md` and `docs/decisions/ADR-001-ot-vs-crdt.md`.

- Explain RGA: every character is a node with an `(lamport@replica)` id and a parent; text is a
  deterministic traversal; deletes are tombstones; **text is a pure function of the operation set**,
  so any order of delivery converges.
- Show the signature test:
  `RgaConvergencePropertyTests.RandomConcurrentEdits_FromMultipleClients_AlwaysConverge`
  (150 iterations, fixed seed 20260903, shuffled orders → identical replicas).
- Contrast: structured checklist uses **field-level LWW** — *"different data shapes, different
  strategy. One-algorithm-for-everything is a red flag."*

## 4. Reconnect & resync (60s) — the part people forget

- Point at `ResyncTests.Offline_client_catches_up_from_checkpoint_then_incrementally` and the
  reconnect sequence diagram in `docs/architecture/architecture.md`.
- Say: *"A client offline for K operations catches up either from a full checkpoint or just the
  missed tail. The server is authoritative; a rejected op returns a defined `Rejected` result, it
  never silently corrupts state."*

## 5. History, diff, restore (45s)

```powershell
# (after signing in — see scripts/demo.ps1 for token retrieval)
GET  /api/v1/documents/{id}/history
GET  /api/v1/documents/{id}/at/{sequence}      # time-travel read
GET  /api/v1/documents/{id}/diff?from=..&to=.. # LCS diff
POST /api/v1/documents/{id}/restore            # forward op, never a rewrite
```

## 6. Abuse control & presence (45s)

- `HubRateLimitTests.Flooding_client_is_rejected_and_then_disconnected` — token bucket + disconnect.
- Presence coalescing (ADR-004): *"20 cursor moves a second become one broadcast; presence is soft
  state and repopulates after a restart."*

## 7. Close on honesty (30s)

Open `docs/security/security-review.md` non-claims and `ADR-005` (Redis backplane UNVERIFIED).
Say: *"I'd rather show you exactly where the edges are than oversell it."*

## Talking-point cheat sheet

- "Convergence is proven, not claimed."
- "Reconciliation is merge, not transform — that's the CRDT payoff."
- "Server authoritative; append-only log + snapshots give time-travel and restore for free."
- "I documented the limitations because that's what makes it trustworthy."
