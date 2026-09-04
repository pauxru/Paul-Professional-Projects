# Test Results

Real, pasted output from `dotnet build -c Release` and `dotnet test -c Release` on the build host
(.NET SDK 10, Windows). Reproduce with the commands shown.

## Build

```text
> dotnet build -c Release

  Idp.Domain -> src\Idp.Domain\bin\Release\net10.0\Idp.Domain.dll
  Idp.Application -> src\Idp.Application\bin\Release\net10.0\Idp.Application.dll
  Idp.Infrastructure -> src\Idp.Infrastructure\bin\Release\net10.0\Idp.Infrastructure.dll
  Idp.UnitTests -> tests\Idp.UnitTests\bin\Release\net10.0\Idp.UnitTests.dll
  Idp.Api -> src\Idp.Api\bin\Release\net10.0\Idp.Api.dll
  Idp.IntegrationTests -> tests\Idp.IntegrationTests\bin\Release\net10.0\Idp.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Test

```text
> dotnet test -c Release

Test run for ...\tests\Idp.UnitTests\bin\Release\net10.0\Idp.UnitTests.dll (.NETCoreApp,Version=v10.0)
Test run for ...\tests\Idp.IntegrationTests\bin\Release\net10.0\Idp.IntegrationTests.dll (.NETCoreApp,Version=v10.0)

Passed!  - Failed:     0, Passed:   112, Skipped:     0, Total:   112, Duration: 198 ms - Idp.UnitTests.dll (net10.0)
Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13, Duration: 3 s   - Idp.IntegrationTests.dll (net10.0)
```

### Summary

| Project | Passed | Failed | Skipped | Total |
| --- | --- | --- | --- | --- |
| `Idp.UnitTests` | 112 | 0 | 0 | 112 |
| `Idp.IntegrationTests` | 13 | 0 | 0 | 13 |
| **Total** | **125** | **0** | **0** | **125** |

## Measured accuracy (diagnostic output)

Printed by `AccuracyTests.Prints_measured_accuracy_report` (run with
`--logger "console;verbosity=detailed"`). These numbers are deterministic and reproduced by the
asserting accuracy tests; see [`accuracy-report.md`](accuracy-report.md).

```text
Passed Idp.UnitTests.AccuracyTests.Prints_measured_accuracy_report
 MEASURE docs=19 classification=100.00% extraction=98.78% extractionCorrect=81/82
 MEASURE_FIELD currency=11/11 (100.0%)
 MEASURE_FIELD dnDate=4/4 (100.0%)
 MEASURE_FIELD dnNumber=4/4 (100.0%)
 MEASURE_FIELD invoiceDate=10/10 (100.0%)
 MEASURE_FIELD invoiceNumber=11/11 (100.0%)
 MEASURE_FIELD poDate=4/4 (100.0%)
 MEASURE_FIELD poNumber=4/4 (100.0%)
 MEASURE_FIELD poReference=4/4 (100.0%)
 MEASURE_FIELD supplierName=14/15 (93.3%)
 MEASURE_FIELD total=15/15 (100.0%)
```

Straight-through-processing, measured over the pristine seeded corpus by
`StpMetricTests` (isolated database, via `GET /api/v1/metrics/stp`):

```text
 MEASURE_STP_CLEAN total=19 processed=19 autoApproved=6 inReview=13 rejected=0 queueDepth=13 rate=31.58%
```

## What the tests cover

- **Accuracy** — classifier accuracy over the corpus (≥ measured threshold), extraction on clean and
  degraded documents, per-field evidence and strategy presence, explainability.
- **Spatial extraction** — table detection (columns/rows, stop-at-totals, cell assignment) and line
  items.
- **Validation rules** — every rule with pass, fail and boundary-tolerance cases (arithmetic,
  subtotal, tax, total, date sanity, duplicate, currency, supplier existence, tax-id format).
- **Three-way match** — clean, over-billing, total-exceeds, partial delivery, over-delivery,
  billed>delivered, and missing-PO cases.
- **Confidence & routing** — aggregation and routing decisions pinned at the threshold boundaries.
- **State machine** — the full valid/invalid/terminal transition matrix plus reprocess behaviour.
- **Review** — claim, concurrent-claim conflict, lease expiry takeover, completed-cannot-claim,
  overdue/priority ordering.
- **Fuzzy supplier matching** — alias/misspelling matches and a negative case; Jaro-Winkler/Levenshtein.
- **Correction feedback loop** — v1 mis-extraction → learn anchor → v2 correct extraction.
- **Export** — retry-then-dead-letter, transient→success, permanent→immediate dead-letter, and
  CSV/formula-injection neutralisation.
- **STP metrics** — pure snapshot computation plus the end-to-end measured rate.
- **API (integration)** — health (anonymous), upload `401`/`403`/`201`, upload too-large `400`,
  wrong-type `400`, list pagination, get-by-id + `404`, fields-with-evidence, review queue + claim,
  exports list, and the STP endpoint.

