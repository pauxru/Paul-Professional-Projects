# Anomaly Evaluation

## Ground truth and protocol
The deterministic generator labels three unexpected resource conditions: a step change (`res-014`), runaway resource (`res-031`), and gradual drift (`res-059`). A recurring month-end batch resource (`res-002`) is a known planned calendar event and is supplied as a suppression, so it remains available as suppressed evidence but is excluded from actionable evaluation.

The detector evaluates day-of-week seasonal references with rolling median/MAD, standard z-score, day-of-week EWMA, CUSUM, and a trend-change score. Daily candidates are deduplicated to a resource-condition detection for this evaluation; this avoids treating a multi-day runaway as dozens of independent incidents.

## Real measured result

Command executed on 2026-09-03:

```powershell
dotnet test tests\CloudCostObservability.UnitTests\CloudCostObservability.UnitTests.csproj -c Release `
  --filter "FullyQualifiedName~SyntheticDataset_BacktestsForecastAndEvaluatesKnownInjectedAnomalies" `
  --logger "console;verbosity=detailed"
```

| Metric | Value |
|---|---:|
| Known unexpected resource conditions | 3 |
| Detected unexpected resource conditions | 3 |
| True positives | 3 |
| False positives | 0 |
| False negatives | 0 |
| Precision | **100.00%** |
| Recall | **100.00%** |

The detailed run emitted 775 raw daily candidates before lifecycle/grouping; that is expected for prolonged step/runaway/drift patterns. The product incident feed groups related candidates by date/grain, and this evaluation counts a resource-condition once.

## What this does and does not show
This is a real result from code executed locally against known, deterministic synthetic labels. It demonstrates that the implementation can catch the injected patterns while excluding the configured recurring batch from actionable precision/recall. It does **not** establish performance or false-positive rates on real provider billing data; real deployments require human-labelled incident history, per-scope calibration, and monitoring of alert volume.
