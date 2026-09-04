# Test Results

Run on 2026-09-03 on Windows with .NET SDK 10.0.400 / target `net10.0`. All datasets are local and synthetic; no Docker, cloud subscription, or external database was used.

## Release build

Command:

```powershell
dotnet build -c Release
```

Actual summary:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:03.41
```

## Release tests

Command:

```powershell
dotnet test -c Release --no-build --logger "console;verbosity=minimal"
```

Actual summary:

```text
Passed!  - Failed:     0, Passed:    50, Skipped:     0, Total:    50, Duration: 1 s - CloudCostObservability.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13, Duration: 1 s - CloudCostObservability.IntegrationTests.dll (net10.0)
```

Total: **63 passed, 0 failed, 0 skipped**.

## Measured synthetic model evaluation

The targeted deterministic evaluation test emitted:

```text
FORECAST_MAPE RunRate=0.93% (21 months), SeasonalAware=0.63% (21 months), LinearRegression=0.89% (21 months)
ANOMALY_EVALUATION TP=3 FP=0 FN=0 Precision=100.00% Recall=100.00% Detected=775
ANOMALY_RESOURCES res-014, res-031, res-059
```

The performance test creates and groups exactly 250,000 local cost rows and asserts completion below ten seconds. It is a bounded synthetic regression test, not a production throughput assertion.
