# Anomaly Evaluation

## Method
This is a deterministic simulator evaluation, not a production accuracy claim. The xUnit test `DeterministicSimulatorFaults_HaveMeasuredDetectionRates` generated 60 baseline one-minute samples for a compressor, then compared 60 fault samples from the same diurnal hour on the next synthetic day. It used the repository's rolling z-score detector with its default `|z| >= 3` threshold. The normal run used a different deterministic random seed and the same seasonal hour.

Command actually run on the Windows/.NET 10 build host:

```powershell
dotnet test tests\Iiot.UnitTests\Iiot.UnitTests.csproj -c Release --no-restore `
  --filter "FullyQualifiedName~DeterministicSimulatorFaults" --logger "console;verbosity=detailed"
```

## Measured output

```text
Anomaly measurement: BearingWear=58/60 (96.7%); Overheating=58/60 (96.7%);
SensorDrift=58/60 (96.7%); Spike=60/60 (100.0%); normal=0/60 (0.0%)
```

| Scenario | Sensor assessed | Detections | Detection rate |
|---|---:|---:|---:|
| Bearing-wear vibration ramp | vibration mm/s RMS | 58 / 60 | 96.7% |
| Overheating ramp | temperature °C | 58 / 60 | 96.7% |
| Sensor pressure drift | pressure bar | 58 / 60 | 96.7% |
| Instant spike | temperature °C | 60 / 60 | 100.0% |
| Normal seasonal-hour samples | temperature °C | 0 / 60 | **0.0% false-positive rate** |

## Interpretation
The first two ramp points of the three gradual faults remain inside the z-score threshold; that is expected for a detector designed not to fire on ordinary noise. The seasonal comparison is important: an earlier non-seasonal fixed-window trial saw normal diurnal movement treated as anomalous, so the evaluation explicitly compares the same synthetic hour. These are small, synthetic, deterministic samples and must not be generalized to physical equipment, safety decisions, or real false-positive rates.

## Detector output
The library exposes not only `IsAnomaly`, but `Detector`, `Statistic`, and an explanation. For example, the z-score reason includes the observed value, mean, standard deviation, and computed z-score; MAD returns the median, MAD, and modified z-score; EWMA returns trend and residual scale; seasonal baseline labels its same-hour context.
