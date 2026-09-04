# ETA Accuracy — Synthetic Simulator Measurement

## Result

Real command executed on the build host:

```powershell
dotnet test tests\SavannaLogistics.UnitTests -c Release `
  --filter "Category=Benchmark" --logger "console;verbosity=detailed"
```

Measured output on 2026-09-03:

```text
ETA_BENCHMARK runs=100 predictions=1000 meanAbsoluteErrorSeconds=41.85 p90AbsoluteErrorSeconds=81.25 maxAbsoluteErrorSeconds=174.07
```

| Metric | Measured value |
|---|---:|
| Synthetic route runs | 100 |
| Predictions compared with actual arrivals | 1,000 |
| Mean absolute error | 41.85 seconds |
| P90 absolute error | 81.25 seconds |
| Maximum absolute error | 174.07 seconds |

## Method

`EtaSimulatorBenchmark_RecordsPredictionAccuracyAgainstSyntheticActualArrivals` uses the repository ETA implementation, not a copied formula. Each deterministic run:

1. selects a ground speed between 35 and 70 km/h;
2. follows a synthetic Nairobi-to-Embakasi straight-line route;
3. adds zero-to-two minutes of signal delay to the actual arrival;
4. emits ten speed observations with ±4 km/h noise;
5. lets the rolling six-sample ETA calculator recompute on every observation;
6. compares each predicted final arrival with that run's simulated actual arrival.

The configured model uses a 1.06 traffic factor and 12 km/h speed floor. The random seed is fixed (`8082026`) so regressions are repeatable.

## Interpretation

The result shows the simple rolling model is internally useful for a controlled synthetic route, not that it has real-world Nairobi accuracy. Error rises when the random signal delay differs from the fixed traffic factor or early noisy observations dominate the rolling average. P90 is the more useful operating measure for SLA buffers than the mean.

## Production accuracy loop

A real deployment would:

- record actual stop arrival only after dwell-confirmed geofence entry;
- segment metrics by route, hour, weekday, weather and vehicle class;
- compare baseline, route/historical and external-traffic models;
- report median, P90/P95, bias and coverage, not only mean error;
- prevent leakage by training/evaluating on time-separated windows;
- monitor model drift and missing/low-quality GPS;
- retain the simple model as an explainable fallback.

## Non-claim

All runs and arrivals are synthetic. No production fleet, client, traffic feed or real driver data was used.
