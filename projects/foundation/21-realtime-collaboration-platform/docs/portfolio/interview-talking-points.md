# Interview Talking Points

Use these to steer a system-design / senior-backend conversation. Each point pairs a claim with where
to prove it in the repo.

## 1. "Why a CRDT and not OT?" (the central decision)

- OT needs a transformation function that satisfies transform properties; getting `transform(ins,
  del)` / `transform(del, del)` right for every adjacency and overlap is where real OT bugs live and
  is hard to test exhaustively.
- RGA makes text a **pure function of the operation set**, so convergence is a *structural* property I
  can test directly: apply the same ops in any order → identical output. Optimistic editing reconciles
  by **merge, not rebase**, removing a whole class of client bugs.
- **Prove it:** `docs/decisions/ADR-001-ot-vs-crdt.md`, `docs/conflict-resolution.md`, and
  `RgaConvergencePropertyTests`.

## 2. "How do you *know* it converges?"

- A randomised property test: M simulated clients, random concurrent operations, applied in
  **shuffled orders** across replicas, asserting identical final documents — **150 iterations, fixed
  seed 20260903** for reproducibility. Plus exhaustive pair tests (ins/ins, ins/del, del/del; same
  position, adjacent, overlapping) and out-of-order buffering tests.
- **Prove it:** `tests/Collab.UnitTests/Crdt/*`.

## 3. "What does it NOT guarantee?" (the honesty question)

- CRDTs guarantee **convergence, not semantic/intention merge**. Concurrent typed runs can
  **interleave** at the character level. Structured LWW **drops the loser** of a concurrent
  single-field write — that's what LWW means. Ordering is **logical (Lamport), not wall-clock**.
  Tombstones accumulate; there's no distributed GC.
- I put this in writing on purpose. **Prove it:** `docs/conflict-resolution.md` §1.6 and §2.3.

## 4. "Different data shapes, different strategies"

- Free text → RGA. A checklist of records → **field-level LWW with version vectors**. Forcing one
  algorithm onto both would be wrong: LWW on prose loses characters; RGA on a single-value field is
  overkill and still needs a tie-break. Choosing per-shape is a senior signal.

## 5. "Server authority vs. peer-to-peer CRDT"

- Pure P2P CRDTs converge *eventually* but can't answer "what's the current version?". I keep the CRDT
  merge semantics **and** a server that assigns a **gap-free monotonic sequence**, persists an
  append-only log, and returns a **defined `Rejected`** result for operations it can't causally place
  — never silent corruption.
- **Prove it:** `CollaborationService.ApplyAsync`, the `(DocumentId, ServerSequence)` unique index,
  `RealtimeCollaborationTests.Server_assigns_contiguous_sequences_under_concurrent_submits`.

## 6. "Reconnect and resync"

- A client offline for K operations catches up **either** from a full checkpoint (snapshot + tail)
  **or** just the missed operation-log tail, chosen by how far behind it is. The causal buffer handles
  any residual reordering.
- **Prove it:** reconnect sequence diagram in `docs/architecture/architecture.md`,
  `ResyncTests.Offline_client_catches_up_from_checkpoint_then_incrementally`.

## 7. "Persistence, history, time-travel, restore"

- Content is **derived**, not a stored blob: `content = replay(log from latest snapshot ≤ k)`. That
  gives history, time-travel, named versions, diff, and **restore-as-forward-operation** (never a
  rewrite) almost for free — and makes corruption recoverable.
- **Prove it:** ADR-003, `SnapshotReplayTests`, `DocumentHistoryTests`,
  `docs/runbooks/document-corruption-recovery.md`.

## 8. "Presence without a broadcast storm"

- Server-side **coalescing**: store latest presence per connection, flush one snapshot per document
  every ~150 ms; idle/evict by heartbeat age. 20 cursor moves/second → one broadcast. Presence is
  **soft state** — a hub restart clears it and clients repopulate within a heartbeat.
- **Prove it:** ADR-004, `PresenceFlusherService`, `PresenceTests`.

## 9. "Abuse control"

- Per-connection **token-bucket** operation limiter with burst allowance and **disconnect after N
  violations**; plus `MaxDocumentLength` / `MaxOperationsPerSubmit`. REST has a separate global
  fixed-window limiter (the hub is excluded — it has its own).
- **Prove it:** `HubRateLimiter`, `HubRateLimitTests.Flooding_client_is_rejected_and_then_disconnected`.

## 10. "Comment anchoring"

- Text-anchored comments carry a character range that is **rebased** as edits shift the text, and are
  flagged **orphaned** (not lost) when their anchored text is deleted.
- **Prove it:** `AnchorRebaser`, `AnchorRebaserTests` (9 cases),
  `CommentAnchorTests.Anchor_follows_inserted_text_then_orphans_when_its_text_is_deleted`.

## 11. "Testing realtime without flakiness"

- Integration tests use **real SignalR clients** over `WebApplicationFactory` (Long Polling transport,
  which authenticates via bearer header and is reliable over the test server) with **hard
  `CancellationTokenSource` deadlines on every realtime test** so the suite always terminates.

## 12. "Where are the edges?" (scope honesty)

- Auth is **demo-grade** (password-less email→JWT), documented in the security review with explicit
  non-claims. **Redis backplane scale-out is configured but UNVERIFIED** (no Redis/Docker on host);
  single node is the only verified topology, and I list exactly what must change for multi-node
  (shared presence, sticky sessions).
- **Prove it:** `docs/security/security-review.md`, `docs/decisions/ADR-005-scale-out-backplane.md`.

## 13. "Why hand-roll the web client?"

- To show the SignalR wire protocol isn't magic: I implemented the JSON hub protocol (handshake,
  `0x1e` framing, invocation/completion/ping) over a raw WebSocket and ported the RGA to JS — **no npm
  dependency** — so two tabs co-edit and the client CRDT is independently fuzzed for convergence.
- **Prove it:** `src/Collab.Api/wwwroot/{rga.js,hub-client.js,app.js}`.
