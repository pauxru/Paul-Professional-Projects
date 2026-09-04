# Runbook — Legal hold

**Purpose**: place, review, and release a legal hold that blocks retention pruning for
specified resources. Legal holds override retention policies unconditionally.

## Applying a legal hold

Preconditions:
- Justification and a ticket/case reference in your ticketing system.
- Authenticated user has the `audit:admin` scope.

```powershell
curl -X POST http://<host>:5019/api/v1/legal-holds `
  -H "Authorization: Bearer <admin token>" `
  -H "Content-Type: application/json" `
  -d '{
        "resourceType": "account",
        "resourceId": "acct-12345",
        "reason": "SAR request from data subject",
        "ticketReference": "LEG-2026-001"
      }'
```

Effect:
- Any audit event whose `(ResourceType, ResourceId)` matches an active hold is skipped by the
  retention pruner. The pruner report includes `legalHoldSkips` count.
- The legal hold itself is stored, timestamped, and the application of the hold is audited via
  the standard ingest path (event type `audit.legal.hold.applied`).

## Reviewing active holds

```powershell
curl http://<host>:5019/api/v1/legal-holds `
  -H "Authorization: Bearer <read token>"
```

The response lists all active holds for the caller's tenant. Filter by resource type or ticket
reference client-side as needed.

## Releasing a legal hold

Release requires `audit:admin`:

```powershell
curl -X DELETE http://<host>:5019/api/v1/legal-holds/{holdId} `
  -H "Authorization: Bearer <admin token>"
```

Effect:
- `ReleasedAt` is set on the row (the hold is not physically deleted — it becomes historical).
- Future retention runs may prune matching events if their retention window has passed.
- The release is audited (`audit.legal.hold.released`).

## Do

- Always attach a real ticket reference so the hold is defensible in front of a regulator.
- Keep hold scopes as narrow as possible (`resourceId` specific, not wildcard).
- Regularly review outstanding holds — a stale hold impedes retention and may itself become a
  finding.

## Don't

- Never manually mutate `LegalHolds` in the database. The API is the only sanctioned path;
  direct DB writes are not audited and may create inconsistencies.
- Never delete `LegalHolds` rows. Release them via the API — the historical record is what
  makes the hold auditable.
