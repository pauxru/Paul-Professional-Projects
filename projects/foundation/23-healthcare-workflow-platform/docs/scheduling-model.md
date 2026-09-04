# Scheduling model

## 1. Vocabulary

- **Slot:** a `(clinicianId, roomId?, startUtc, endUtc)` tuple during which a specified
  appointment type can be booked.
- **Working pattern:** a `(clinicianId, facilityId, dayOfWeek, startLocal, endLocal)` tuple
  describing recurring availability.
- **Leave:** a `(clinicianId, from, to, kind)` range during which a clinician is off.
- **Closure:** a `(facilityId, from, to, reason)` range during which the facility is closed.
- **Buffer:** a per-appointment-type turnaround minutes value applied after the appointment.

## 2. Slot generation

Given `(facilityId, clinicianId?, date, appointmentTypeId)`:

```
patterns  ← workingPatterns for (clinician, facility, dayOfWeek(date))
patterns  ← patterns \ leavePeriods that overlap date
patterns  ← patterns \ closures for facility overlapping date
patterns  ← patterns ∩ operatingHours(facility, dayOfWeek(date))

apptType  ← appointmentType(appointmentTypeId)
duration  ← apptType.DurationMinutes
buffer    ← apptType.BufferMinutes
required  ← apptType.RequiredCapability

rooms     ← rooms in facility where required in Capabilities
existing  ← appointments for (clinician, or any(rooms)) on date with Status not in (Cancelled, NoShow)

for each windowStart in patterns.enumerate(15-minute grid):
    candidateEnd = windowStart + duration + buffer
    for each room in rooms:
        conflict = existing.any(a =>
            a.ClinicianId == clinician
            OR a.RoomId == room)
          AND intervals overlap
        if not conflict:
            yield Slot(clinician, room, windowStart.UTC, (windowStart + duration).UTC)
            break
```

Complexity: `O(candidates * (rooms + existing_ordered))`. Existing appointments are indexed
by `(ClinicianId, StartUtc)` and `(RoomId, StartUtc)` so the overlap check is efficient.

## 3. Timezone correctness

- Working patterns and operating hours are **local** (`TimeOnly` on `DayOfWeek`).
- Candidate `windowStart` is expressed in the facility's IANA tz, then converted to UTC via
  `TimeZoneInfo.FindSystemTimeZoneById(facility.TimezoneId)`.
- Because the algorithm reasons in local business time, DST transitions are handled
  implicitly: an "10:00 local" slot on the DST-transition day converts to whichever UTC time
  `TimeZoneInfo` chooses for that local time, which is the intended behaviour for business
  operations.
- Integration test `AvailabilityTests.Slot_Times_Are_Consistent_Across_A_Dst_Transition_London_Facility`
  verifies this end-to-end.

## 4. Concurrency guarantees

At the application layer we perform an overlap check before saving. That check is *advisory*
— under contention two independent requests may both pass their local check. The **actual**
guarantee is at the DB layer:

```sql
CREATE UNIQUE INDEX ux_appointments_clinician_slot_active
    ON appointments (ClinicianId, StartUtc)
    WHERE "Status" NOT IN (7, 8);   -- 7 = NoShow, 8 = Cancelled

CREATE UNIQUE INDEX ux_appointments_room_slot_active
    ON appointments (RoomId, StartUtc)
    WHERE "Status" NOT IN (7, 8);
```

- SQLite supports partial (filtered) unique indexes; Postgres does too.
- On a duplicate insert, SQLite raises `SQLITE_CONSTRAINT` (error 19). We translate this
  to a `DomainException("appointment.conflict", ...)` in
  `AppointmentBookingService.BookAsync`. The endpoint returns `409 Conflict`.
- **Cancelled and no-show rows do not lock the slot**, so rebooking a cancelled slot is
  permitted. This is what the integration test
  `AppointmentLifecycleTests.Cancel_Then_Book_Same_Slot_Is_Allowed` verifies.

## 5. Overbooking policy

`AppointmentType.AllowsOverbooking` bypasses the *application* overlap check (e.g. for a
walk-in triage type that intentionally shares a clinician slot). The DB unique index still
prevents identical start times, so overbookings must be staggered by at least the index-key
resolution — an intentional design choice that keeps auditability of the resulting queue
straightforward.

## 6. Recurring series

`RecurringSeries` stores an RRULE-like pattern plus a set of exception dates. The
materialisation background job (out of scope for the current build) enumerates future
occurrences and books individual `Appointment` rows. Cancelling a single occurrence adds
its date to the exception set.

## 7. Waitlist auto-offer

On `AppointmentBookingService.CancelAsync`, `WaitlistService.OfferSlotAsync` is invoked. It
picks the highest-priority `WaitlistEntry` matching the appointment type and clinician /
facility, marks it `Offered`, and sets `AcceptanceDeadlineUtc = now + AcceptanceWindow`
(default 4 hours). A scheduled job (`WaitlistService.ExpireOffersAsync`) transitions expired
offers back to `Active`.

## 8. Reminder scheduling

For each appointment `A`, and each configured `LeadTime L`, upsert one `Reminder` row per
`(A.Id, L, Channel)` at `SendAtUtc = A.StartUtc - L`. Because the unique index makes the
upsert idempotent, calling `ScheduleAsync(A.Id)` twice inserts on the first call and returns
zero on the second (verified by
`ReminderTests.ScheduleAsync_Is_Idempotent`).
