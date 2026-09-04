# Screenshots Needed

Screenshots/recordings to capture for the portfolio write-up. None exist in the repo yet (this is a
checklist, not a claim that they were taken). Capture against a locally-running instance
(`dotnet run --project src/Idp.Api`, seeded corpus, `http://localhost:5009`).

## Priority captures

1. **STP metric response** — `GET /api/v1/metrics/stp` JSON showing `totalDocuments: 19`,
   `autoApproved: 6`, `reviewQueueDepth: 13`, `straightThroughRate: 0.3158`. *This is the headline
   number — capture it first.*
2. **Review console — queue** — the prioritised queue at `/`, showing value/age/confidence ordering
   and SLA aging.
3. **Review console — document detail** — a single document with fields, **confidence colouring**,
   **evidence span/box** display, and the approve/correct/reject controls.
4. **Extracted fields with evidence** — `GET /api/v1/documents/{id}/fields` showing `value`,
   `confidence`, `strategy` and `box` per field.
5. **A failed validation** — a document's `validations` array with a `Fail`/`Warn` outcome, message
   and implicated fields (ideally a three-way-match warning on a partial delivery).
6. **Test run** — terminal showing `dotnet test -c Release` with the passing summary (125 tests).
7. **Accuracy print** — terminal showing the `AccuracyTests` diagnostic lines (classification 100%,
   extraction 98.78%, per-field breakdown).

## Secondary captures

8. **Auth denial** — a `401` (no token) and a `403` (wrong permission) ProblemDetails response.
9. **Upload validation** — a `400` for an oversized upload and for a wrong content-type.
10. **Export list** — `GET /api/v1/exports` showing statuses, attempts and an ERP reference; ideally
    one `DeadLettered` record to illustrate the dead-letter path.
11. **Correction feedback** — before/after: v1 field mis-extracted, correction applied, v2 extracted
    correctly (can be shown from the `CorrectionFeedbackTests` output).
12. **Architecture diagrams** — rendered Mermaid diagrams from the README (container view, state
    machine, review sequence) for slides.

## Notes for capture

- Use the seeded Development corpus so numbers match the accuracy report exactly.
- Redact nothing — all data is synthetic/fictional by design.
- For the review console, pick a document with a genuine validation failure so confidence colouring
  and evidence display are meaningful.
- Prefer short screen recordings (GIF/MP4) for the queue → claim → correct → approve → export flow;
  stills for JSON responses.
