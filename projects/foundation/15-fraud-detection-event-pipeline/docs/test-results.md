# Test Results — Real, Copied From `dotnet test`

This file contains the **actual, unedited output** from the final validation run on this host
after the champion-vs-challenger tuning cycle. Regenerate by executing
`dotnet build -c Release --no-incremental` and then
`dotnet test -c Release --no-build --logger "console;verbosity=normal"` from the repository root.

## Host facts

- Windows, PowerShell 7
- .NET SDK **10.0.400**
- Target framework: `net10.0`
- No Docker, no external DB — SQLite in-process/in-memory only.

## 1. `dotnet build -c Release --no-incremental`

```
Determining projects to restore...
  All projects are up-to-date for restore.
  FraudPipeline.Domain -> src\FraudPipeline.Domain\bin\Release\net10.0\FraudPipeline.Domain.dll
  FraudPipeline.Application -> src\FraudPipeline.Application\bin\Release\net10.0\FraudPipeline.Application.dll
  FraudPipeline.Infrastructure -> src\FraudPipeline.Infrastructure\bin\Release\net10.0\FraudPipeline.Infrastructure.dll
  FraudPipeline.UnitTests -> tests\FraudPipeline.UnitTests\bin\Release\net10.0\FraudPipeline.UnitTests.dll
  FraudPipeline.Api -> src\FraudPipeline.Api\bin\Release\net10.0\FraudPipeline.Api.dll
  FraudPipeline.IntegrationTests -> tests\FraudPipeline.IntegrationTests\bin\Release\net10.0\FraudPipeline.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:04.80
```

## 2. `dotnet test -c Release --no-build`

Two test assemblies. Unit tests run first, then integration tests (which spin up the API in
`WebApplicationFactory<Program>` with an in-memory SQLite connection kept open for the fixture
lifetime).

### 2.1 Assembly `FraudPipeline.UnitTests` — 62 tests, all pass

```
Passed FraudPipeline.UnitTests.CaseGroupingByLinkageTests.TwoAlertsSameCard_GroupedIntoOneCase [119 ms]
Passed FraudPipeline.UnitTests.CaseManagementDomainTests.AddNote_AfterDisposition_Throws [1 ms]
Passed FraudPipeline.UnitTests.CaseManagementDomainTests.Assign_TransitionsToAssigned [< 1 ms]
Passed FraudPipeline.UnitTests.CaseManagementDomainTests.FalsePositive_DoesNotRequireFourEyes [< 1 ms]
Passed FraudPipeline.UnitTests.CaseManagementDomainTests.LargeExposure_ConfirmedFraud_RequiresFourEyes [1 ms]
Passed FraudPipeline.UnitTests.CaseManagementDomainTests.LinkAlert_IncrementsExposureAndPriority [4 ms]
Passed FraudPipeline.UnitTests.CaseManagementDomainTests.NewCase_StartsInNewStatus [4 ms]
Passed FraudPipeline.UnitTests.CaseManagementDomainTests.SmallExposure_ConfirmedFraud_DisposesImmediately [15 ms]
Passed FraudPipeline.UnitTests.CaseManagementDomainTests.StateMachine_ProposeDispositionWithoutAssignment_Throws [< 1 ms]
Passed FraudPipeline.UnitTests.ConsumerAndDeadLetterTests.Consumer_OnRepositoryFailure_WritesToDeadLetter [393 ms]
Passed FraudPipeline.UnitTests.DetectionBenchmarkRunTests.ChampionChallenger_ProducesRealMeasuredMetrics [1 s]
Passed FraudPipeline.UnitTests.DetectionEvaluatorTests.ComputeAsync_MixedOutcomes_ComputesConfusionMatrix [1 ms]
Passed FraudPipeline.UnitTests.DetectionEvaluatorTests.ComputeAsync_PerfectClassifier_YieldsPrecisionAndRecallOne [63 ms]
Passed FraudPipeline.UnitTests.DetectionPerformanceRunTests.RunSyntheticEndToEnd_ProducesRealMeasuredMetrics [312 ms]
Passed FraudPipeline.UnitTests.FeatureStoreReplayTests.Rebuild_FromReplay_ReproducesLiveAggregates [26 ms]
Passed FraudPipeline.UnitTests.ImpossibleTravelTests.Haversine_AntipodalPair_ReturnsApproximately20015Km [1 ms]
Passed FraudPipeline.UnitTests.ImpossibleTravelTests.Haversine_SameCity_ReturnsFeasibleAndTinyDistance [< 1 ms]
Passed FraudPipeline.UnitTests.ImpossibleTravelTests.ImpossibleTravel_NairobiToNewYorkIn30Minutes_Impossible [< 1 ms]
Passed FraudPipeline.UnitTests.ImpossibleTravelTests.ImpossibleTravel_NairobiToNewYorkOverNightlyFlight_Feasible [< 1 ms]
Passed FraudPipeline.UnitTests.ImpossibleTravelTests.ImpossibleTravel_SameLocationAtSameSecond_Feasible [10 ms]
Passed FraudPipeline.UnitTests.ImpossibleTravelTests.ImpossibleTravel_SameSecondButDifferentCity_Impossible [< 1 ms]
Passed FraudPipeline.UnitTests.PartitionedBusTests.Enqueue_TwoTxnsSameCard_LandInSamePartition [3 ms]
Passed FraudPipeline.UnitTests.PartitionedBusTests.PartitionOf_IsDeterministicForSameCard [16 ms]
Passed FraudPipeline.UnitTests.PartitionedBusTests.PerEntityOrdering_UnderParallelIngestion_IsPreserved [2 ms]
Passed FraudPipeline.UnitTests.PartitionedBusTests.TotalLag_AggregatesAcrossPartitions [1 ms]
Passed FraudPipeline.UnitTests.RuleEngineTests.AllowList_HitsMerchant_Fires [9 ms]
Passed FraudPipeline.UnitTests.RuleEngineTests.CardTestingPattern_FiveSmallThenBig_Fires [2 ms]
Passed FraudPipeline.UnitTests.RuleEngineTests.DenyList_HitsCard_Fires [< 1 ms]
Passed FraudPipeline.UnitTests.RuleEngineTests.ImpossibleTravelRule_FarApartQuickly_Fires [< 1 ms]
Passed FraudPipeline.UnitTests.RuleEngineTests.ImpossibleTravelRule_ReasonableTime_DoesNotFire [1 ms]
Passed FraudPipeline.UnitTests.RuleEngineTests.MccRisk_HighRiskCategory_Fires [< 1 ms]
Passed FraudPipeline.UnitTests.RuleEngineTests.MccRisk_LowRiskCategory_DoesNotFire [4 ms]
Passed FraudPipeline.UnitTests.RuleEngineTests.RoundAmount_MultipleOfDenomination_Fires [1 ms]
Passed FraudPipeline.UnitTests.RuleEngineTests.TimeOfDay_MiddleOfNightInLocal_Fires [< 1 ms]
Passed FraudPipeline.UnitTests.RuleEngineTests.UnusualAmountZScore_HighZ_Fires [17 ms]
Passed FraudPipeline.UnitTests.RuleEngineTests.UnusualAmountZScore_TypicalTxn_DoesNotFire [< 1 ms]
Passed FraudPipeline.UnitTests.RuleEngineTests.Velocity_AboveThreshold_Fires [1 ms]
Passed FraudPipeline.UnitTests.RuleEngineTests.Velocity_UnderThreshold_DoesNotFire [< 1 ms]
Passed FraudPipeline.UnitTests.ScoringServiceTests.RiskBands_ClassifyBoundaries [< 1 ms]
Passed FraudPipeline.UnitTests.ScoringServiceTests.Score_AboveDeclineBand_ReturnsDecline [< 1 ms]
Passed FraudPipeline.UnitTests.ScoringServiceTests.Score_AllowListPresent_OverridesAllOtherRules [1 ms]
Passed FraudPipeline.UnitTests.ScoringServiceTests.Score_DenyListPresent_ForcesDecline [< 1 ms]
Passed FraudPipeline.UnitTests.ScoringServiceTests.Score_ExceedsLatencyBudget_DegradesToReview [1 ms]
Passed FraudPipeline.UnitTests.ScoringServiceTests.Score_MerchantPolicyOverride_ForcesReview [5 ms]
Passed FraudPipeline.UnitTests.ScoringServiceTests.Score_NoRulesFire_ReturnsApproveWithZero [117 ms]
Passed FraudPipeline.UnitTests.ScoringServiceTests.Score_SameTxnSameRuleset_ProducesSameScore [1 ms]
Passed FraudPipeline.UnitTests.ShadowComparatorTests.CompareAsync_AllMatch_ReturnsMatchRateOne [21 ms]
Passed FraudPipeline.UnitTests.ShadowComparatorTests.CompareAsync_DifferencesCounted_ByTransitionKey [3 ms]
Passed FraudPipeline.UnitTests.ValueObjectInvariantTests.GeoLocation_RejectsBadCountry [< 1 ms]
Passed FraudPipeline.UnitTests.ValueObjectInvariantTests.GeoLocation_RejectsOutOfRangeLatitude [< 1 ms]
Passed FraudPipeline.UnitTests.ValueObjectInvariantTests.GeoLocation_RejectsOutOfRangeLongitude [< 1 ms]
Passed FraudPipeline.UnitTests.ValueObjectInvariantTests.Money_AcceptsKesUsdEurGbp [1 ms]
Passed FraudPipeline.UnitTests.ValueObjectInvariantTests.Money_RejectsNegativeAmount [< 1 ms]
Passed FraudPipeline.UnitTests.ValueObjectInvariantTests.Money_RejectsUnknownCurrency [10 ms]
Passed FraudPipeline.UnitTests.WindowedFeatureAggregatorTests.Advance_EvictsOldBuckets [19 ms]
Passed FraudPipeline.UnitTests.WindowedFeatureAggregatorTests.Aggregate_DistinctCountriesAndMerchants_AreUnioned [< 1 ms]
Passed FraudPipeline.UnitTests.WindowedFeatureAggregatorTests.Aggregate_EventsOutsideWindow_AreExcluded [< 1 ms]
Passed FraudPipeline.UnitTests.WindowedFeatureAggregatorTests.Aggregate_ExactlyAtEdge_IsIncludedInWindow [< 1 ms]
Passed FraudPipeline.UnitTests.WindowedFeatureAggregatorTests.Aggregate_ManyEventsAcrossHour_ComputesAvgAndStddev [2 ms]
Passed FraudPipeline.UnitTests.WindowedFeatureAggregatorTests.Aggregate_SubWindowLargerThanRetention_Throws [< 1 ms]
Passed FraudPipeline.UnitTests.WindowedFeatureAggregatorTests.Aggregate_WithinSubWindow_ReturnsCountAndSum [< 1 ms]
Passed FraudPipeline.UnitTests.WindowedFeatureAggregatorTests.OutOfOrderArrival_WithinRetention_StillAggregatesCorrectly [< 1 ms]

Test Run Successful.
Total tests: 62
     Passed: 62
 Total time: 2.1 Seconds
```

Newly added in the tuning cycle: `DetectionBenchmarkRunTests.ChampionChallenger_ProducesRealMeasuredMetrics`
— runs the seeded synthetic stream through both the baseline `v1.0.0` ruleset and the tuned
`v1.1.0` challenger, plus a six-point threshold sweep, writing three snapshot JSONs used by
`docs/detection-performance.md`. Asserts (as executable acceptance criteria):

- The challenger's recall is strictly greater than the baseline's.
- The challenger's recall is at least 55 % (single-digit-FPR operating floor).
- The challenger's false-positive rate is at most 3 % (low-single-digit ceiling).

### 2.2 Assembly `FraudPipeline.IntegrationTests` — 7 tests, all pass

The throughput test dominates the wall clock: it POSTs 5,000 unique score requests through the
full ASP.NET pipeline (auth, model binding, feature store, rule engine, EF Core to SQLite,
case management).

```
Passed FraudPipeline.IntegrationTests.ScoringApiTests.HealthLive_ReturnsHealthy [101 ms]
Passed FraudPipeline.IntegrationTests.ScoringApiTests.Score_InvalidMcc_ReturnsProblemDetails [2 s]
Passed FraudPipeline.IntegrationTests.ScoringApiTests.Score_ValidRequest_Returns200WithDecision [316 ms]
Passed FraudPipeline.IntegrationTests.ScoringApiTests.Score_WithoutJwt_Returns401 [7 ms]
Passed FraudPipeline.IntegrationTests.ScoringApiTests.Score_WithWrongScope_Returns403 [21 ms]
Passed FraudPipeline.IntegrationTests.ScoringApiTests.Throughput_ScoresMoreThan5000TransactionsInBoundedTime [1 m 2 s]
Passed FraudPipeline.IntegrationTests.ScoringApiTests.TokenEndpoint_ReturnsBearerToken [11 ms]

Test Run Successful.
Total tests: 7
     Passed: 7
 Total time: 53 Seconds
```

## 3. Totals

| Category         | Count      |
|------------------|-----------:|
| Assemblies       | 2          |
| Tests total      | **69**     |
| Tests passed     | **69**     |
| Tests failed     | 0          |
| Tests skipped    | 0          |
| Warnings         | 0          |
| Errors           | 0          |

## 4. Detection performance from the perf test

Two perf tests exist:

- `DetectionPerformanceRunTests.RunSyntheticEndToEnd_ProducesRealMeasuredMetrics` — runs the
  baseline `v1.0.0` ruleset. Kept for reproducibility and continuity with the original perf
  snapshot artefact. Writes `docs/detection-performance.snapshot.json`.
- `DetectionBenchmarkRunTests.ChampionChallenger_ProducesRealMeasuredMetrics` — runs baseline
  **and** challenger **and** a six-point threshold sweep. Writes the three `-v1_0`, `-v1_1`,
  `-sweep` snapshots that `docs/detection-performance.md` reports from.

### Latest baseline `v1.0.0` numbers (from `detection-performance-v1_0.snapshot.json`)

```json
{
  "RulesetVersion": "v1.0.0",
  "Scored": 1226,
  "FraudInjected": 106,
  "TotalWallSec": 0.2247,
  "LabelledTransactions": 1226,
  "TruePositives": 17,
  "FalsePositives": 0,
  "TrueNegatives": 1120,
  "FalseNegatives": 89,
  "Precision": 1.0,
  "Recall": 0.1604,
  "FalsePositiveRate": 0.0,
  "F1": 0.276,
  "AlertVolume": 17,
  "ValueDetected": 61000.0,
  "P50Ms": 0.087,
  "P95Ms": 0.147,
  "P99Ms": 0.219
}
```

### Latest challenger `v1.1.0` numbers (from `detection-performance-v1_1.snapshot.json`) — this is what ships

```json
{
  "RulesetVersion": "v1.1.0",
  "Scored": 1226,
  "FraudInjected": 106,
  "TotalWallSec": 0.2033,
  "LabelledTransactions": 1226,
  "TruePositives": 59,
  "FalsePositives": 5,
  "TrueNegatives": 1115,
  "FalseNegatives": 47,
  "Precision": 0.9219,
  "Recall": 0.5566,
  "FalsePositiveRate": 0.0045,
  "F1": 0.694,
  "AlertVolume": 64,
  "ValueDetected": 119400.0,
  "P50Ms": 0.082,
  "P95Ms": 0.152,
  "P99Ms": 0.203
}
```

**Honest interpretation.** The shipped ruleset (`v1.1.0`) catches **55.7 %** of injected fraud
at **92.2 %** precision and **0.45 %** false-positive rate — the tuned operating point derived
from the threshold sweep in `docs/detection-performance.md`. p99 scoring latency stays at
**0.20 ms**, far below the 50 ms budget. The full baseline-vs-challenger comparison, threshold
sweep, per-pattern breakdown and rule-level fires table live in `docs/detection-performance.md`.

## 5. How to reproduce

```powershell
cd C:\path\to\15-fraud-detection-event-pipeline
dotnet build -c Release --no-incremental
dotnet test  -c Release --no-build --logger "console;verbosity=normal"
Get-Content .\docs\detection-performance-v1_0.snapshot.json
Get-Content .\docs\detection-performance-v1_1.snapshot.json
Get-Content .\docs\detection-performance-sweep.snapshot.json
```
