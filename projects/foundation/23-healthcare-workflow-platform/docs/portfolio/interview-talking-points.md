# Interview talking points

## 1. "Walk me through the hardest problem you solved on this project."

The concurrent-booking guarantee. A naïve booking service checks for overlap in the
application layer and inserts. Under contention, two requests can both see "no overlap"
and both insert. The only reliable fix is a database constraint that expresses the
invariant.

I chose a **filtered unique index** on `(ClinicianId, StartUtc)` and `(RoomId, StartUtc)`
filtered to `Status NOT IN (7, 8)` — i.e. NoShow and Cancelled don't lock the slot. This
works on SQLite and Postgres identically.

Then, at the application layer, I catch the resulting `DbUpdateException`, verify it is a
constraint violation (via `SqliteErrorCode == 19` or the message text), and translate it to
a domain exception `appointment.conflict` which the middleware surfaces as `409 Conflict`.

I proved it works with an integration test that runs two `Task.Run` bookings against the
same slot with independent DbContexts and asserts exactly one succeeds.

## 2. "How does break-glass work?"

Two headers: `X-Break-Glass: true` and `X-Break-Glass-Justification: <non-empty>`. The
`ClinicalAccessGuard` sees the pair, bypasses the care-relationship check, returns
`AccessDecision(allowed=true, reason="break_glass", breakGlass=true)`. The endpoint records
an audit row with `BreakGlass = true` and the justification. A background report surfaces
these events for weekly review. Missing justification denies (403).

The design tension is latency vs oversight. Approval-based break-glass would be safer but
unusable in an emergency; header-based is usable but relies on the audit + review loop to
keep staff honest. That review loop is documented in
`docs/runbooks/break-glass-review.md`.

## 3. "How did you ensure the notes are truly append-only?"

Three layers:

1. `ClinicalNote` has private setters and factory methods (`CreateInitial`,
   `CreateAmendment`); the entity itself refuses mutation.
2. `Encounter.AmendNote` never mutates the original — it creates a new `ClinicalNote` with
   the same `RootNoteId` and `Version = existing + 1`.
3. `AppDbContext.SaveChanges` calls `RejectImmutableWrites()` which throws on
   `EntityState.Modified` (with actual property changes) or `EntityState.Deleted` for
   `ClinicalNote` and `AuditEvent`. An integration test writes directly to the tracked
   entity via reflection and asserts the throw.

## 4. "Why a modular monolith?"

For a single-clinic operational domain, cross-aggregate transactions are frequent (book →
audit → schedule reminders → maybe waitlist offer). A monolith makes those atomic. The
cost is deployability — I can't ship changes to reminders independently of bookings — but
in a portfolio project that trade-off is clear and defensible. The module boundaries
between Domain / Application / Infrastructure / Api are enforced by project references, so
if I did want to split into microservices later I would know exactly where to cut.

## 5. "How would you migrate to Postgres in production?"

Set `ConnectionStrings:Default` to a `Host=...` string; `Program.cs` selects `UseNpgsql`.
Every schema feature I depend on (filtered unique indexes, DateTimeOffset stored as ticks,
integer enum columns) works on both providers. Integration tests would be run against a
Postgres container in CI. The application code doesn't change.

## 6. "How do you know your tests are meaningful?"

They exercise the full stack against an open in-memory SQLite via `WebApplicationFactory<Program>`
— real EF Core, real transactions, real JWT authentication, real ABAC guard, real audit
sink. Not mocks. Every "signature" behaviour from the brief has at least one dedicated
integration test — concurrent booking, DST correctness, break-glass audit, append-only
notes, reminder idempotency, referral SLA escalation, duplicate patient detection.

Total: 29 unit + 33 integration = 62 tests. All pass on `dotnet test -c Release`.
