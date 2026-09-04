# Runbook — Double-booking incident

## Symptom
Two appointments were created for the same clinician + start time, or the DB unique
constraint fired and a booking request returned `500 Internal Server Error` instead of
`409 Conflict`.

## Immediate triage (first 10 minutes)

1. Identify the offending appointments:
   ```sql
   SELECT a1.Id, a2.Id, a1.ClinicianId, a1.StartUtc
   FROM appointments a1
   JOIN appointments a2
     ON a1.ClinicianId = a2.ClinicianId
    AND a1.StartUtc    = a2.StartUtc
    AND a1.Id          < a2.Id
    AND a1.Status NOT IN (7, 8)   -- active
    AND a2.Status NOT IN (7, 8);
   ```

2. Correlate to the request logs by `AppointmentId` and `X-Correlation-Id`:
   ```
   grep -F "appointment:<Id>" logs/*.log
   ```

3. Confirm which patient was booked first. Preserve both audit rows.

4. Cancel the duplicate via `POST /api/v1/appointments/{id}/cancel` with reason
   `Other` and notes `"duplicate created by concurrency race; cancelled by ops"`. **Do not
   delete rows manually** — the audit trail must remain intact.

## Root cause categories

- The filtered unique index `ux_appointments_clinician_slot_active` was missing (schema
  drift). Verify:
  ```sql
  SELECT name, sql FROM sqlite_master WHERE type = 'index' AND name LIKE 'ux_appointments%';
  ```
  Both `ux_appointments_clinician_slot_active` and `ux_appointments_room_slot_active` should
  be present.

- The exception was not translated to `409 Conflict`. Check
  `AppointmentBookingService.BookAsync` — the `catch (DbUpdateException) when
  (IsConcurrencyConflict(ex))` clause must be active.

- Overbooking flag on the appointment type is enabled. Confirm:
  ```sql
  SELECT Code, Name, AllowsOverbooking FROM appointment_types WHERE Id = ...;
  ```
  If overbooking is intentional, the "duplicate" is expected.

## Follow-up

- Add the correlation ids of both requests to the incident review.
- Verify `healthcare.booking.latency` histogram for the affected timeframe — a spike often
  correlates with contention.
- Review the concurrent-booking integration test (`ConcurrentBookingTests`) still passes:
  ```
  dotnet test -c Release --filter FullyQualifiedName~ConcurrentBookingTests
  ```

## Communication

- Notify the patient(s) via the receptionist team; do not use the reminder channel for this.
- File a clinical-governance note if the appointment had already been checked in.
