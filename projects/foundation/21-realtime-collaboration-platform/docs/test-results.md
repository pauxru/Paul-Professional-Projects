# Test Results

**This is real, pasted output.** Reproduce with the commands shown below.

- Host: Windows, .NET SDK **10.0.400**, target `net10.0`
- Date: 2026-09-03
- Configuration: **Release**
- Infrastructure required: **none** (EF Core + SQLite; SignalR over the in-memory test server)

## Commands

```powershell
dotnet build -c Release
dotnet test  -c Release
```

## Build output (real)

```
Determining projects to restore...
  All projects are up-to-date for restore.
  Collab.Domain -> ...\src\Collab.Domain\bin\Release\net10.0\Collab.Domain.dll
  Collab.Application -> ...\src\Collab.Application\bin\Release\net10.0\Collab.Application.dll
  Collab.Infrastructure -> ...\src\Collab.Infrastructure\bin\Release\net10.0\Collab.Infrastructure.dll
  Collab.UnitTests -> ...\tests\Collab.UnitTests\bin\Release\net10.0\Collab.UnitTests.dll
  Collab.Api -> ...\src\Collab.Api\bin\Release\net10.0\Collab.Api.dll
  Collab.IntegrationTests -> ...\tests\Collab.IntegrationTests\bin\Release\net10.0\Collab.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:02.54
```

## Test summary (real)

```
Passed!  - Failed:     0, Passed:    30, Skipped:     0, Total:    30, Duration: 255 ms - Collab.UnitTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    25, Skipped:     0, Total:    25, Duration: 3 s - Collab.IntegrationTests.dll (net10.0)
```

| Project | Passed | Failed | Skipped | Total |
|---|---|---|---|---|
| Collab.UnitTests | 30 | 0 | 0 | 30 |
| Collab.IntegrationTests | 25 | 0 | 0 | 25 |
| **Total** | **55** | **0** | **0** | **55** |

Every realtime integration test carries a hard `CancellationTokenSource` deadline so the suite always
terminates even if a client fails to converge.

## Unit tests (30) — domain correctness

CRDT convergence (every transform/merge pair, out-of-order buffering, randomised property test),
structured LWW, diff engine, and comment anchor rebasing:

```
Comments.AnchorRebaserTests.Caret_RebasesAroundEdits
Comments.AnchorRebaserTests.DeleteBeforeAnchor_ShiftsLeft
Comments.AnchorRebaserTests.DeleteCoveringEntireAnchor_Orphans
Comments.AnchorRebaserTests.DeleteExactlyAnchor_Orphans
Comments.AnchorRebaserTests.DeletePartiallyOverlapping_ShrinksAnchor
Comments.AnchorRebaserTests.InsertAfterAnchor_LeavesUnchanged
Comments.AnchorRebaserTests.InsertBeforeAnchor_ShiftsBoth
Comments.AnchorRebaserTests.InsertInsideAnchor_GrowsEnd
Comments.AnchorRebaserTests.OrphanedAnchor_IsNotFurtherModified
Crdt.RgaConvergencePropertyTests.RandomConcurrentEdits_FromMultipleClients_AlwaysConverge
Crdt.RgaDocumentTests.AdjacentInserts_Converge
Crdt.RgaDocumentTests.BuildDelete_RemovesRange
Crdt.RgaDocumentTests.BuildInsert_AtIndex_ProducesExpectedText
Crdt.RgaDocumentTests.ConcurrentDeleteSameElement_IsIdempotentAndConverges
Crdt.RgaDocumentTests.ConcurrentInsertAndDelete_Converge
Crdt.RgaDocumentTests.ConcurrentInsertSamePosition_ConvergesDeterministically
Crdt.RgaDocumentTests.DuplicateOperation_IsIgnored
Crdt.RgaDocumentTests.ExportImportState_RoundTrips
Crdt.RgaDocumentTests.OutOfOrderDelete_IsBufferedUntilTargetArrives
Crdt.RgaDocumentTests.OutOfOrderInsert_IsBufferedThenAppliedWhenParentArrives
Crdt.RgaDocumentTests.OverlappingDeletes_Converge
Diffing.DiffEngineTests.DiffChars_Deletion_IsDetected
Diffing.DiffEngineTests.DiffChars_IdenticalText_IsAllEqual
Diffing.DiffEngineTests.DiffChars_InsertionInMiddle_IsDetected
Diffing.DiffEngineTests.DiffLines_ReplacedLine_ProducesInsertAndDelete
Diffing.DiffEngineTests.ReconstructingFromDiff_AlwaysYieldsBothSides
Structured.StructuredDocumentTests.ConcurrentFieldWrites_HighestStampWins
Structured.StructuredDocumentTests.RandomConcurrentStructuredMerges_Converge
Structured.StructuredDocumentTests.RemoveWinsWhenNewerThanAdd_AndAddWinsWhenNewer
Structured.StructuredDocumentTests.VersionVector_DetectsConcurrency
```

> The signature invariant test is
> `RgaConvergencePropertyTests.RandomConcurrentEdits_FromMultipleClients_AlwaysConverge`:
> 150 iterations, fixed seed **20260903**, M simulated clients, operations applied in shuffled
> orders, asserting all replicas converge to an identical document.

## Integration tests (25) — real SignalR clients over `WebApplicationFactory`

Real `Microsoft.AspNetCore.SignalR.Client` connections (Long Polling transport) against the in-memory
test server, plus REST via `HttpClient`:

```
CommentAnchorTests.Anchor_follows_inserted_text_then_orphans_when_its_text_is_deleted
DocumentHistoryTests.Diff_reports_inserted_content_between_sequences
DocumentHistoryTests.Restore_creates_a_new_forward_version_without_rewriting_history
DocumentHistoryTests.Time_travel_reads_the_document_at_an_earlier_sequence
HubAuthorizationTests.Non_member_cannot_join_a_document
HubAuthorizationTests.Unauthenticated_connection_is_refused
HubAuthorizationTests.Viewer_can_join_but_cannot_submit_an_edit
HubRateLimitTests.Flooding_client_is_rejected_and_then_disconnected
NotificationTests.Mention_is_delivered_live_to_a_connected_user
NotificationTests.Mention_is_persisted_for_an_offline_user_and_read_on_return
PresenceTests.Cursor_updates_are_coalesced_and_broadcast
PresenceTests.Presence_propagates_when_a_second_client_joins
RealtimeCollaborationTests.Concurrent_inserts_at_the_same_position_converge_identically
RealtimeCollaborationTests.Concurrent_structured_field_writes_resolve_by_last_writer_wins
RealtimeCollaborationTests.Server_assigns_contiguous_sequences_under_concurrent_submits
RealtimeCollaborationTests.Two_clients_co_editing_a_text_document_converge
RestApiTests.Creating_a_document_with_an_invalid_title_is_400
RestApiTests.Creating_a_document_with_an_unknown_type_is_400
RestApiTests.Health_endpoints_report_ready
RestApiTests.Non_member_reading_a_document_is_403
RestApiTests.Unauthenticated_request_is_401
RestApiTests.Workspace_and_document_are_listed_for_a_member
ResyncTests.Joining_returns_the_authoritative_current_state
ResyncTests.Offline_client_catches_up_from_checkpoint_then_incrementally
SnapshotReplayTests.Snapshot_plus_log_replay_reproduces_the_document_exactly
```

## Additional verification (web client CRDT port)

The browser RGA port (`src/Collab.Api/wwwroot/rga.js`) was independently checked under Node with the
same seed philosophy: deterministic tie-break, **200 shuffled convergence trials**, and a
`fromState` round-trip — all passed. This is a development-time check (not part of the .NET suite);
the .NET RGA is the authoritative, CI-gated implementation.
