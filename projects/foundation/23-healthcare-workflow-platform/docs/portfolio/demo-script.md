# Demo script (7 minutes)

## Setup
1. `dotnet run --project src\Healthcare.Api` → API on `https://localhost:5023`.
2. `scripts\demo.ps1 -Step Setup` seeds a fictional facility, clinician, and patient.

## Demo

**(0:00 – 0:45) The pitch.**
> "This is the scheduling and clinical-workflow engine of an outpatient clinic. I'll walk
> through booking, break-glass access, and append-only clinical notes. All data is
> synthetic — no real patients, no real hospitals — and no HIPAA or GDPR certification is
> claimed."

**(0:45 – 2:00) Slot search and book.**
- `GET /api/v1/availability?facilityId=…&date=2026-09-07&appointmentTypeId=…`
- Point out that the engine has already filtered out closed rooms, leave, holidays,
  buffers, and existing bookings — no cache, no precomputed table.
- Book the first slot with `POST /api/v1/appointments`.

**(2:00 – 3:00) Concurrent booking.**
- Show `ConcurrentBookingTests.cs`. Two parallel `Task.Run` bookings, exactly one succeeds.
- Show the filtered unique index in `AppointmentConfiguration.cs`.
- Rerun `dotnet test --filter ConcurrentBooking` live if time permits.

**(3:00 – 4:00) Access control.**
- As `Receptionist`: `GET /api/v1/encounters/{id}` → 403. Point out that receptionists see
  demographics but never clinical notes.
- As `Clinician` **without** a care relationship: 403.
- As `Clinician` **with** an appointment: 200, note contents visible.
- Show the audit row for that read.

**(4:00 – 5:30) Break-glass.**
- Repeat the "no care relationship" call, this time with `X-Break-Glass: true` +
  `X-Break-Glass-Justification: "suspected sepsis"`.
- Response: 200 with data.
- Immediately hit `GET /api/v1/reports/access-anomalies` — the event is visible.
- Note the `healthcare.break_glass.total` counter.

**(5:30 – 6:30) Notes append-only.**
- Add a note. Amend it. Show `GET /api/v1/encounters/{id}/notes` returns both versions
  with `Version = 1` and `Version = 2`.
- Attempt to `PUT` directly against the tracked entity in a scratch script — DbContext
  raises `InvalidOperationException("… is append-only …")`.

**(6:30 – 7:00) Wrap.**
> "Everything you saw is backed by tests. `dotnet test -c Release` runs 62 tests — 29 unit,
> 33 integration — and they all pass. No mocks, no fakes for the DB — real SQLite in memory
> for integration. The full write-up is in the README and six ADRs. I want to draw your
> attention to the break-glass runbook — because in a real hospital, the design of the
> review process matters as much as the code."
