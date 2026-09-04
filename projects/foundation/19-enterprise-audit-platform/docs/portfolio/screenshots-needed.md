# Screenshots to capture for the portfolio write-up

These are the recommended screenshots to accompany the portfolio case study. Take them from the
running dev environment (`dotnet run` + PowerShell) and store them in `docs/portfolio/images/`.

## 1. Repository structure

- Editor pane showing the `src/` and `tests/` layout.
- Emphasise the four-project Clean Architecture split.

## 2. Boot log

- Terminal showing `Now listening on: http://localhost:5019`.
- Include the OpenTelemetry console lines and the seeder log ("seeded schema: user.login").

## 3. Ingest response

- The full JSON of a `/api/v1/events` 201 response, showing `sequenceNumber` and `chainHash`.

## 4. Verification report — clean chain

- The `/api/v1/verify` JSON with `isValid: true`, `checksPerformed`, `brokenAtSequence: null`.

## 5. Verification report — tampered chain

- The same call after step 6 of `scripts/demo.ps1`, with `isValid: false`,
  `brokenAtSequence: 3`, and the human-readable `reason`.

## 6. Merkle inclusion proof

- Response from `/api/v1/events/{id}/proof` showing the `MerklePathStep[]`, `merkleRoot`, and
  the signature.

## 7. Evidence pack

- The JSON of a small evidence pack. Highlight the `manifest.bundleHash` and the top-level
  `signature`.
- Followed by the `/api/v1/exports/evidence/verify` response with `valid: true`.

## 8. Privileged-access anomaly report

- A response from `/api/v1/reports/privileged-access` showing an `out-of-hours` and a
  `permission-escalation` anomaly (the second is emitted by the seeder into the example
  tenant).

## 9. Interceptor block

- xUnit test output showing `Interceptor_BlocksUpdateOfAuditEventThroughEfSaveChanges` passing
  with the `DomainException(AppendOnlyViolation)` assertion.

## 10. Test summary

- Terminal output of `dotnet test -c Release` finishing with `Passed! - Failed: 0, Passed: 62`.

## Design notes

- Use a dark editor theme for readability at web-scale.
- Anonymise any host names / paths — the personal Windows path is fine but the interviewer
  doesn't need to see other repos.
- Keep image widths ≤ 1600 px.
