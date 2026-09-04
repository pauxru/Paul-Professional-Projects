# Runbook — Ruleset Rollback

## Signal

- Deployed a new ruleset and something is wrong: alert flood (see `alert-flood.md`), or precision
  drop (see `docs/detection-performance.md` metrics after activation), or an analyst reports a
  legitimate merchant is being blocked.

## Confirm

1. Read `GET /api/v1/rulesets` — note the current `IsActive` version and its `ActivatedAt` timestamp.
2. Read the previous `IsActive` — it will be the row with the next-most-recent `ActivatedAt`.
3. If in doubt about which was the previous, check `docs/detection-performance.snapshot.json`
   for the last-known-good `RulesetVersion` and compare.

## Roll back

### Step 1 — Get an approver in the room

Ruleset activation should be a four-eyes decision, even though the API does not currently enforce
it. Notify a second engineer via chat and record their acknowledgement.

### Step 2 — Activate the previous version

```powershell
# Get a token with risk:admin
$body = @{ subject='oncall'; scopes=@('risk:admin') } | ConvertTo-Json
$tok  = (Invoke-RestMethod -Method Post -Uri http://localhost:5015/api/v1/auth/token `
        -ContentType 'application/json' -Body $body).accessToken

# Activate the prior version
Invoke-RestMethod -Method Post `
    -Uri "http://localhost:5015/api/v1/rulesets/$prevVersion/activate" `
    -Headers @{ Authorization = "Bearer $tok" }
```

### Step 3 — Confirm

```powershell
Invoke-RestMethod -Uri http://localhost:5015/api/v1/rulesets `
    -Headers @{ Authorization = "Bearer $tok" } |
    Where-Object IsActive | Format-Table Version, ActivatedAt
```

The version that comes back must be the previous known-good one.

## Verify the rollback took

- **Sanity score**: `POST /api/v1/transactions/score` with a canonical benign request and check
  that `rulesetVersion` on the response matches the version you just activated.
- **Reproducibility**: same request twice = same score = same decision, and the `rulesetVersion`
  string is stable. If it isn't, you have a caching/singleton bug — file an incident.
- **Metrics**: `GET /api/v1/metrics/detection` should return the pre-incident precision within
  the noise band. If not, the rollback wasn't the fix — start investigating a different regression.

## After rollback

- Do **not** delete the offending ruleset row. Keep it as evidence. The `Rulesets` table is
  append-only by convention (see `docs/database-schema.md`).
- File an ADR-style postmortem noting *why* the ruleset misbehaved: threshold too aggressive,
  weight typo, missing test fixture, etc.
- Add a shadow-mode simulation test that would have caught it: `POST /api/v1/rulesets/simulate`
  with the labelled dataset and assert the precision/recall delta is above a floor.
