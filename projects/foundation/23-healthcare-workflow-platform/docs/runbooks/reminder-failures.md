# Runbook — Reminder failures

## Symptom

Either:
- `healthcare.reminder.failed` counter is elevated (if wired into a dashboard), or
- The dead-letter query returns rows:
  ```sql
  SELECT COUNT(*) FROM reminders WHERE Status = 3;  -- 3 = DeadLetter
  ```

## Immediate triage

1. Identify the affected channel:
   ```sql
   SELECT Channel, COUNT(*) FROM reminders
   WHERE Status = 3 AND SendAtUtc > <last-24h ticks>
   GROUP BY Channel;
   ```

2. If **SMS**, check the `InMemorySmsReminderChannel` log length via the health endpoint
   (production would check the SMS-provider API status).

3. If **Email**, ditto for `InMemoryEmailReminderChannel`.

## Recovery

For rows in the retry range (Status = Failed):
```sql
UPDATE reminders
   SET Status = 0,   -- Pending
       Attempts = 0
 WHERE Status = 1    -- Failed
   AND SendAtUtc > <last-24h ticks>;
```

For rows in DeadLetter, evaluate case by case — do NOT bulk-retry without confirming the
appointments still make sense to remind on.

## Root causes

- Channel outage: transient. `ReminderService.DispatchDueAsync` will retry on the next
  cycle. If persistent, mark the channel disabled in configuration.
- Opt-out flag flipped for many patients at once: check `patients.OptedOutOfReminders`
  distribution.
- Clock skew: `FakeClock` in tests is intentional; **production** should never see
  `IClock` behave anachronistically. Verify the process's system time.
- Idempotency index missing: check
  ```sql
  SELECT name, sql FROM sqlite_master
  WHERE type = 'index' AND tbl_name = 'reminders';
  ```
  A UNIQUE `(AppointmentId, LeadTime, Channel)` index must exist.

## Prevention

- Alarming: connect the `healthcare.reminder.failed` counter to a monitoring system.
- Weekly review of DLQ row count.
- Regular canary reminders — schedule one to yourself and confirm you receive it.

## Related

- `Healthcare.Application/Reminders/ReminderService.cs` — the service.
- `Healthcare.Infrastructure/Adapters/InMemory*ReminderChannel.cs` — the channels.
- Integration tests: `ReminderTests.cs`.
