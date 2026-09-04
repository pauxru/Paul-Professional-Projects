# 90-second demo script

_Setting: video walk-through or live interview demo._

## 0:00–0:10 — The pitch

> "This is an audit and compliance event store. It's what a bank would use when it needs to
> prove to an auditor that a specific action really happened, that no one changed the record
> afterwards, and that they can produce evidence of it on demand — without asking the auditor
> to just take their word for it. It's all self-directed engineering; there's no third-party
> certification claimed."

## 0:10–0:20 — Boot & health check

```powershell
dotnet run --project src/AuditPlatform.Api -c Release
```

Show the console log line `Now listening on: http://localhost:5019`.

Hit `/health/ready` from another terminal → 200 OK.

## 0:20–0:35 — Ingest

Run `./scripts/demo.ps1` up to step 4. The script mints a JWT, registers the `user.login`
schema (automatic via seeder), and ingests five events with distinct `ClientEventId`s.

Point out in the response payload:

- `sequenceNumber` — the per-tenant chain position.
- `chainHash` — the newly-computed link.
- `wasDuplicate=false` — idempotency indicator.

## 0:35–0:50 — Verify

The demo script POSTs `/api/v1/verify`. Show `"isValid": true`.

> "That's SHA-256 over the canonical JSON for every event, chained together, all matching. The
> chain would break if anyone touched anything."

## 0:50–1:05 — Tamper & re-verify

Step 6 of the demo runs a direct `UPDATE` against the SQLite DB (bypassing the API entirely).

Re-verification returns `isValid: false` with `brokenAtSequence: 3` and a reason string like
`"contentHash mismatch — payload was tampered with"`.

> "So even if someone gets shell access to the database, the audit log can prove they touched
> it. That's the whole point."

## 1:05–1:20 — Evidence pack

Step 8: `POST /api/v1/checkpoints` to build a Merkle checkpoint, then
`POST /api/v1/exports/evidence` to produce the signed pack.

Open `demo-evidence-pack.json`. Point out:

- The events list.
- The `checkpoints` block with `merkleRoot` and `signatureBase64`.
- The `manifest.bundleHash`.
- The top-level `signature`.

Verify it round-trips:

```powershell
Invoke-RestMethod -Method POST -Uri http://localhost:5019/api/v1/exports/evidence/verify `
  -Headers @{Authorization="Bearer $token"} `
  -Body ($pack | ConvertTo-Json -Depth 10) -ContentType application/json
# → { valid = True }
```

## 1:20–1:30 — Close

> "Everything you just saw — canonical JSON, hash chain, Merkle tree with inclusion proofs,
> RSA-signed checkpoints, the append-only interceptor, retention that preserves the chain
> across tombstoning — is all built from primitives with a full 62-test suite. No mocks in the
> integrity tests. No hand-waving. `docs/integrity-model.md` in the repo shows how to write a
> verifier in a different language, if you needed to."
