# Test Results — Example Bank Digital Banking Ledger

Real output from a clean `dotnet build -c Release` followed by `dotnet test -c Release`. Nothing in
this file is fabricated — it is the verbatim tool summary.

- **Environment**: Windows (10.0.26200), .NET SDK **10.0.400**, target `net10.0`
- **Date**: 2026-09-03
- **Command**: `dotnet build ExampleBank.Ledger.slnx -c Release` then
  `dotnet test ExampleBank.Ledger.slnx -c Release`
- **Result**: ✅ **Build succeeded (0 warnings, 0 errors)** · ✅ **99 tests passed, 0 failed, 0 skipped**

## Summary

| Test project | Passed | Failed | Skipped | Total | Duration |
|--------------|-------:|-------:|--------:|------:|---------:|
| `ExampleBank.Ledger.UnitTests` | 80 | 0 | 0 | 80 | ~0.1 s |
| `ExampleBank.Ledger.IntegrationTests` | 19 | 0 | 0 | 19 | ~5 s |
| **Total** | **99** | **0** | **0** | **99** | — |

The minimum required by the build spec is 35 tests; this project ships **99**.

## Build output (verbatim tail)

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:02.38
```

All six projects compiled: `Domain`, `Application`, `Infrastructure`, `Api`, `UnitTests`,
`IntegrationTests`.

## Test output (verbatim tail)

```
Test run for ...\tests\ExampleBank.Ledger.UnitTests\bin\Release\net10.0\ExampleBank.Ledger.UnitTests.dll (.NETCoreApp,Version=v10.0)
Test run for ...\tests\ExampleBank.Ledger.IntegrationTests\bin\Release\net10.0\ExampleBank.Ledger.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    80, Skipped:     0, Total:    80, Duration: 99 ms - ExampleBank.Ledger.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    19, Skipped:     0, Total:    19, Duration: 5 s - ExampleBank.Ledger.IntegrationTests.dll (net10.0)
```

## What the tests cover

### Unit tests (80) — pure domain invariants & calculations
- **Money** — construction, zero/sign, `FromMajor` scale rejection (`ledger.sub_minor_precision`),
  checked add/subtract/negate, mixed-currency rejection, major/minor conversions.
- **Currency** — known-currency lookup, unknown rejection, scale / minor-units-per-major.
- **Account** — normal-balance side per type, balance derivation, available = balance − held,
  overdraft boundaries (`EnsureCanWithdraw`), place/release hold effects, freeze/activate/close
  guards, **close-with-non-zero-balance rejected**, control-account & non-active posting rejection.
- **JournalEntry / Posting** — ≥2 postings required, **balanced-per-currency enforced**,
  **mixed-currency rejected** for non-FX, positive-amount postings, sealing assigns sequence + hash,
  **double-seal rejected** (append-only).
- **Hash chain** — genesis, `SHA-256(prev ‖ content)` linkage, **tamper detection**.
- **FeeSchedule** — fixed, percentage (bps rounding), tiered selection, min/max caps.
- **Interest** — accrual for **Actual/365** and **30/360** across a period with `FakeClock`.
- **FX** — quote value conservation and conserved sub-unit remainder.

### Integration tests (19) — full HTTP via `WebApplicationFactory<Program>` on real SQLite
- Health; **401** unauthenticated; **403** wrong scope.
- Transfer happy path (funds move, hash chain seals); **negative amount → 422**;
  **insufficient funds → 422**; **close with balance → 400**.
- **Idempotency replay** — repeated key returns the original entry, posts once.
- Trial balance sums to zero per currency; integrity chain valid + balances reconcile.
- Statement tie-out (`opening + Σ movements = closing`).
- Reversal full, partial, and **exceeding-original rejected (400)**.
- FX conversion keeps trial balance zero.
- **Four concurrency tests** (the headline proof):
  1. 150 parallel transfers, same two accounts — total conserved, derived balances correct.
  2. 150 bidirectional `A→B`/`B→A` transfers — no deadlock (ordered locking).
  3. 50 concurrent withdrawals vs limited funds — **exactly 10 succeed, never overdraw**.
  4. 50 parallel duplicate idempotency keys — **exactly one posting**.

## Reproduce

```powershell
dotnet build ExampleBank.Ledger.slnx -c Release
dotnet test  ExampleBank.Ledger.slnx -c Release
```

No external infrastructure is required — the integration tests create and seed a temporary SQLite
database per test class.
