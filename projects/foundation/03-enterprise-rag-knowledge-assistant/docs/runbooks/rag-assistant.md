# Runbook — RAG Knowledge Assistant

Operational reference for anyone who has to keep the RAG service running or
diagnose it in production. Written to be read at 3 AM.

## Service summary

- **Name:** Enterprise RAG Knowledge Assistant
- **Endpoint:** `http://localhost:5003` (dev), configurable Kestrel binding
- **Health:** `/health/live`, `/health/ready`
- **Metrics:** OpenTelemetry, console exporter by default; add
  `OTLP` exporter in production
- **Logs:** Structured (Serilog) with a `CorrelationId` enriched from the
  `X-Correlation-Id` request header when present

## Common failure modes

### 1. `dotnet build` fails immediately with `NU1101` / `NU1102`

The NuGet feed is unreachable. Check the internal proxy status and re-run
`dotnet restore`. Do not add `--version` pins; the feed's default resolution
is what we test against.

### 2. Boot fails with `Refusing to start in Production with default dev JWT signing key.`

The environment variable `Jwt__SigningKey` (or `Jwt:SigningKey`) is still
the shipped placeholder. Rotate to a 32+ character HS256 secret loaded from
your secret store.

### 3. Every query returns "I don't have enough information..."

Retrieval is falling below `Rag:MinRetrievalScore` or the grounding checker
is rejecting the answer. Steps:

1. Hit `/api/v1/eval/runs` as an admin. Confirm the golden-set metrics are
   still healthy — Recall@5 ≥ 0.7 and refusal accuracy ≥ 0.8.
2. If eval metrics collapsed, the vector store is empty or misconfigured.
   Check the boot logs for `Seed: N document(s) ingested.` and confirm N > 0.
3. If eval metrics look fine, temporarily lower `Rag:MinSupportRatio` from
   0.4 to 0.25 and observe. If answers start flowing, tighten the threshold
   again and inspect the failing queries — usually a chunker regression.

### 4. Chat endpoint 500s with `DbUpdateConcurrencyException`

Historically caused by tracking an already-persisted session as Modified.
Current code path uses `AppendMessagesAsync` with an `ExecuteUpdateAsync`
that bumps `UpdatedAt`. If this reappears, the fix is *not* to add retries
— it is to check that the message-append path is not calling `.Update()`
on the graph.

### 5. `429 Too Many Requests`

Two causes:

- Rate limiter — configured by `RateLimiting:PermitsPerMinute` (default 120
  per identity per minute). Bumps do not require a restart if `IOptions`
  are reloaded on change; today they are read once at request time.
- Budget guard — configured by `Budget:DailyLimitUsd` / `DailyRequestLimit`.
  Check `UsageEntryRecord` rows for the tenant in question. Delete stale
  dev entries if needed, or raise the cap.

### 6. Restricted document leaked in an answer (**Sev 1**)

1. Save the offending query + response and freeze the request logs
   (`CorrelationId` grep).
2. Confirm from `documents.acl_*` columns that the document's ACL matches
   the expected roles/classification.
3. Confirm the caller's JWT claims — decode and check `role` + `classification`
   claims match their entitlement.
4. Deploy a rollback if a recent change touched `SqliteVectorStore`,
   `AclPostFilter`, or the retriever adapters.
5. File an incident post-mortem; extend the leak test in
   `QueryEndpointsTests` with the reproducer.

## Reindex a document

```powershell
curl -X POST "http://localhost:5003/api/v1/documents/$id/reindex?strategy=SentenceAware" -H "Authorization: Bearer $token"
```

Reindex is idempotent by content hash but chunk boundaries can change if you
switch strategies. Prefer sentence-aware in production.

## Rotate a prompt

```powershell
curl -X POST http://localhost:5003/api/v1/admin/prompts -H "Authorization: Bearer $adminToken" -H "Content-Type: application/json" -d '{"name":"rag.answer","version":"v2","body":"..."}'
```

The new version is inserted as active; older versions are deactivated
automatically. Every answer records the prompt version it used.

## Backups

SQLite file lives at `RagAssistant.Api/rag.db` by default. Snapshotting the
file while the service is up requires WAL-safe copy (`sqlite3 rag.db
".backup rag.bak"`) — a plain file copy can corrupt an open connection.
