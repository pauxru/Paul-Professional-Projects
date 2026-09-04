# Test results

**Command**: `dotnet test -c Release --nologo`
**Host**: Windows, .NET SDK 10.0.400 targeting `net10.0`
**Date captured**: build session finalised at commit time

## Summary

| Project                                     | Total | Passed | Failed | Skipped | Duration      |
|---------------------------------------------|-------|--------|--------|---------|---------------|
| `AuditPlatform.UnitTests`                   |   35  |   35   |   0    |    0    | 1.0 s         |
| `AuditPlatform.IntegrationTests`            |   27  |   27   |   0    |    0    | 1 min 33 s    |
| **Total**                                   | **62**| **62** | **0**  | **0**   | ~1 min 34 s   |

## Passed test list (verbatim from `dotnet test` output)

```
  Passed AuditPlatform.UnitTests.HashChainTests.LinkHash_ChangesWithPayload [12 ms]
  Passed AuditPlatform.UnitTests.MerkleTreeTests.VerifyPath_RejectsWrongRoot [11 ms]
  Passed AuditPlatform.UnitTests.PrivilegedAccessRuleTests.OutOfHoursRule_FlagsCriticalEventAtMidnight [8 ms]
  Passed AuditPlatform.UnitTests.FakeClockTests.FakeClock_Advances [8 ms]
  Passed AuditPlatform.UnitTests.UuidV7Tests.NewGuid_SortsByTimestamp [14 ms]
  Passed AuditPlatform.UnitTests.HashChainTests.GenesisHash_IsTenantSpecific [< 1 ms]
  Passed AuditPlatform.UnitTests.FakeClockTests.FakeClock_CanSet [< 1 ms]
  Passed AuditPlatform.UnitTests.MerkleTreeTests.BuildAndVerifyPath_RoundTrips_ForEveryLeaf_OddCount [< 1 ms]
  Passed AuditPlatform.UnitTests.MerkleTreeTests.VerifyPath_RejectsForgedLeaf [< 1 ms]
  Passed AuditPlatform.UnitTests.HashChainTests.LinkHash_SurvivesTombstoning [1 ms]
  Passed AuditPlatform.UnitTests.MerkleTreeTests.ComputeRoot_IsDeterministic [< 1 ms]
  Passed AuditPlatform.UnitTests.HashChainTests.HexRoundTrip_Works [< 1 ms]
  Passed AuditPlatform.UnitTests.MerkleTreeTests.BuildAndVerifyPath_RoundTrips_ForEveryLeaf_EvenCount [< 1 ms]
  Passed AuditPlatform.UnitTests.HashChainTests.LinkHash_IsDeterministic [< 1 ms]
  Passed AuditPlatform.UnitTests.HashChainTests.LinkHash_ChangesWithPreviousHash [< 1 ms]
  Passed AuditPlatform.UnitTests.SchemaCompatibilityTests.RemovingField_IsIncompatible [18 ms]
  Passed AuditPlatform.UnitTests.UuidV7Tests.ExtractUnixMs_ReturnsOriginalTimestamp [4 ms]
  Passed AuditPlatform.UnitTests.RedactionServiceTests.Standard_Clearance_Redacts_Before_And_After [20 ms]
  Passed AuditPlatform.UnitTests.SchemaCompatibilityTests.Validate_ReportsMissingRequiredField [1 ms]
  Passed AuditPlatform.UnitTests.SchemaCompatibilityTests.AddingRequiredField_IsIncompatible [< 1 ms]
  Passed AuditPlatform.UnitTests.CanonicalJsonTests.Serialize_HasNoInsignificantWhitespace [21 ms]
  Passed AuditPlatform.UnitTests.CanonicalJsonTests.Serialize_PreservesArrayOrder [< 1 ms]
  Passed AuditPlatform.UnitTests.CanonicalJsonTests.Serialize_HandlesUnicodeStringsIdentically [< 1 ms]
  Passed AuditPlatform.UnitTests.CanonicalJsonTests.Serialize_NestedObjectSortsRecursively [< 1 ms]
  Passed AuditPlatform.UnitTests.SchemaCompatibilityTests.Validate_AcceptsValidPayload [1 ms]
  Passed AuditPlatform.UnitTests.SchemaCompatibilityTests.MakingOptionalRequired_IsIncompatible [< 1 ms]
  Passed AuditPlatform.UnitTests.CanonicalJsonTests.Serialize_HandlesNullBooleanAndArrays [< 1 ms]
  Passed AuditPlatform.UnitTests.SchemaCompatibilityTests.AddingOptionalField_IsCompatible [< 1 ms]
  Passed AuditPlatform.UnitTests.RedactionServiceTests.Investigator_Clearance_Sees_Before_And_After [3 ms]
  Passed AuditPlatform.UnitTests.UuidV7Tests.NewGuid_SetsVersionAndVariantBits [3 ms]
  Passed AuditPlatform.UnitTests.CanonicalJsonTests.Serialize_IntegerNumbersHaveNoFraction [< 1 ms]
  Passed AuditPlatform.UnitTests.CanonicalJsonTests.Serialize_DecimalNumbersStripTrailingZeros [< 1 ms]
  Passed AuditPlatform.UnitTests.CanonicalJsonTests.Serialize_IsIdempotent [< 1 ms]
  Passed AuditPlatform.UnitTests.SchemaCompatibilityTests.RetypingField_IsIncompatible [1 ms]
  Passed AuditPlatform.UnitTests.CanonicalJsonTests.Serialize_SortsObjectKeys_Lexicographically [< 1 ms]
```

```
  Passed AuditPlatform.IntegrationTests.AuthTests.Ingest_WithoutToken_Returns401 [1 s]
  Passed AuditPlatform.IntegrationTests.HealthTests.Health_Ready_ReturnsOk [1 s]
  Passed AuditPlatform.IntegrationTests.HealthTests.Health_Live_ReturnsOk [6 ms]
  Passed AuditPlatform.IntegrationTests.AuthTests.Ingest_WithReadOnlyToken_Returns403 [21 ms]
  Passed AuditPlatform.IntegrationTests.AuthTests.DevTokenEndpoint_MintsToken_AndRoundtripsToRead [591 ms]
  Passed AuditPlatform.IntegrationTests.IngestTests.Ingest_SingleEvent_ProducesChainedEvent [2 s]
  Passed AuditPlatform.IntegrationTests.IngestTests.Ingest_WithDuplicateClientEventId_IsIdempotent [60 ms]
  Passed AuditPlatform.IntegrationTests.ChainVerificationTests.Verify_DeletedEvent_DetectsChainBreak [2 s]
  Passed AuditPlatform.IntegrationTests.MetaAuditAndThroughputTests.Reading_AuditLog_ProducesMetaAuditEvent_WithoutInfiniteRecursion [2 s]
  Passed AuditPlatform.IntegrationTests.IngestTests.Ingest_BatchWithMixedResults_ReportsPerEvent [60 ms]
  Passed AuditPlatform.IntegrationTests.IngestTests.Ingest_WithoutRegisteredSchema_ReturnsUnprocessable [45 ms]
  Passed AuditPlatform.IntegrationTests.InterceptorAndCheckpointTests.Checkpoint_HasSignatureThatVerifies [2 s]
  Passed AuditPlatform.IntegrationTests.RetentionAndLegalHoldTests.Retention_LegalHold_BlocksPruning [2 s]
  Passed AuditPlatform.IntegrationTests.ChainVerificationTests.Verify_CleanChain_Passes [214 ms]
  Passed AuditPlatform.IntegrationTests.ExportsAndReportsTests.Export_EvidencePack_VerifiesRoundTrip [3 s]
  Passed AuditPlatform.IntegrationTests.ChainVerificationTests.Verify_ReorderedEvent_Detected [107 ms]
  Passed AuditPlatform.IntegrationTests.QueryAndPaginationTests.Query_KeysetPagination_IsStableUnderNewInserts [3 s]
  Passed AuditPlatform.IntegrationTests.ExportsAndReportsTests.PrivilegedAccessReport_FlagsOutOfHoursAndEscalations [146 ms]
  Passed AuditPlatform.IntegrationTests.ChainVerificationTests.Verify_TamperedPayload_DetectsAtCorrectSequence [102 ms]
  Passed AuditPlatform.IntegrationTests.RetentionAndLegalHoldTests.Retention_PrunesOldEvents_AndChainStillVerifies [233 ms]
  Passed AuditPlatform.IntegrationTests.InterceptorAndCheckpointTests.Checkpoint_InclusionProof_WithForgedLeaf_IsInvalid [242 ms]
  Passed AuditPlatform.IntegrationTests.ExportsAndReportsTests.Export_Ndjson_ReturnsEventsAsLines [66 ms]
  Passed AuditPlatform.IntegrationTests.InterceptorAndCheckpointTests.Interceptor_BlocksDeleteOfAuditEventThroughEfSaveChanges [60 ms]
  Passed AuditPlatform.IntegrationTests.QueryAndPaginationTests.Query_FreeTextSearch_FindsEvents [164 ms]
  Passed AuditPlatform.IntegrationTests.InterceptorAndCheckpointTests.Interceptor_BlocksUpdateOfAuditEventThroughEfSaveChanges [22 ms]
  Passed AuditPlatform.IntegrationTests.InterceptorAndCheckpointTests.Checkpoint_ThenInclusionProof_VerifiesAllEvents [49 ms]
  Passed AuditPlatform.IntegrationTests.MetaAuditAndThroughputTests.Throughput_Ingests_TwentyThousandEvents_WithinBudget [1 m 28 s]
```

## Final `dotnet test` summary lines

```
Passed!  - Failed:     0, Passed:    35, Skipped:     0, Total:    35, Duration: 1.0476 Seconds - AuditPlatform.UnitTests.dll (net10.0)
Passed!  - Failed:     0, Passed:    27, Skipped:     0, Total:    27, Duration: 1.5464 Minutes  - AuditPlatform.IntegrationTests.dll (net10.0)

Test Run Successful.
Total tests: 62
     Passed: 62
```

## Notes on the throughput test

`Throughput_Ingests_TwentyThousandEvents_WithinBudget` ingests 20 000 events via 40 batches of
500 through the real HTTP pipeline (`WebApplicationFactory` + in-memory shared-cache SQLite).
Wall-clock: 1 min 28 s on the dev host, well inside the 4-minute budget the test asserts. This
is not a substitute for a production throughput measurement; it is a proof that ingest holds
up under bulk load without ordering or idempotency drift.

## Notes on the integrity tests

The four signature integrity tests (`Verify_CleanChain_Passes`,
`Verify_TamperedPayload_DetectsAtCorrectSequence`, `Verify_DeletedEvent_DetectsChainBreak`,
`Verify_ReorderedEvent_Detected`) all issue `db.Database.ExecuteSqlRawAsync(...)` to mutate the
DB *outside* the interceptor. That's the exact scenario chain verification is designed to
catch: an operator or attacker who can bypass the API. In every case the chain report contains
`isValid: false`, `brokenAtSequence: <expected>`, and a human-readable `reason`.
