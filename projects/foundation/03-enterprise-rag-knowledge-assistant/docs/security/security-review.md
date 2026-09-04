# Security Review — Enterprise RAG Knowledge Assistant

Scope: the code in this repository as of the last commit. Every finding is
about our own code — we do not review third-party libraries beyond checking
their intended use.

Owner: any principal engineer picking this up. Update the "Verified against"
line when running through the checklist.

**Verified against:** `dotnet build -c Release --no-incremental` succeeds
with 0 warnings; `dotnet test -c Release` passes 70/70; the eval harness
runs end to end with the default local provider.

---

## Threat model (STRIDE, tailored)

### S — Spoofing

- **Authn.** JWT bearer, HS256, issuer + audience validated, `ClockSkew` 15 s.
  Signing key comes from configuration (`Jwt:SigningKey`) — the API refuses
  to boot in `Production` if the key still starts with `dev-only`.
- **Dev token endpoint.** `POST /api/v1/auth/token` is only mapped when the
  environment is not `Production`. The `HmacDevTokenIssuer` never runs in
  production and never signs a token with a longer lifetime than requested.
- **Anonymous user.** `UserPrincipal.Anonymous` (classification `Public`,
  role `anonymous`) is the fallback if a request reaches the retrieval layer
  outside an authenticated context. Retrieval will honour ACLs against this
  identity — restricted content is never returned.

### T — Tampering

- **Correlation id.** `CorrelationIdMiddleware` accepts an incoming
  `X-Correlation-Id` header but validates it against a strict character set
  before logging. Untrusted correlation ids cannot inject log lines.
- **Prompt versioning.** Prompts are content-hashed on registration.
  Attempting to change a prompt's body while re-using its version identifier
  yields a `409 Conflict`. Answering records the exact prompt id + version
  it used, so replaying is deterministic.

### R — Repudiation

- **Feedback + usage ledgers.** Every answer records prompt version and
  cited chunk ids into either the feedback record (when the user rates it)
  or the usage ledger (per request). Both include a monotonic timestamp.

### I — Information disclosure (the big one for RAG)

- **Permission-aware retrieval (ADR 0004).** ACLs are enforced at the store
  and again as a post-filter. The `Query_RestrictedDoc_LeaksNothing`
  integration test asks the LTIP compensation question as an unauthorised
  user and asserts:
    - No citation carries a restricted document title;
    - No forbidden substring (e.g. `LTIP`, `3.5 million`) appears in the
      answer.
- **List endpoint.** `GET /api/v1/documents` calls
  `IDocumentRepository.ListForUserAsync`, which filters by ACL in EF. The
  `ListDocuments_FiltersByAcl` test asserts that a regular employee cannot
  see restricted documents in the list.
- **Get endpoint.** `GET /api/v1/documents/{id}` returns 403 for a
  restricted document the user does not have access to
  (`GetDocument_UnauthorizedClassification_Returns403`).
- **PII in logs.** `CorrelationIdMiddleware` and `SecurityHeadersMiddleware`
  do not log request bodies or query strings by default. Serilog is
  configured at `Information` — no message enricher touches `HttpContext.User`
  claims beyond the correlation id.

### D — Denial of service

- **Rate limiter.** Global `PartitionedRateLimiter` (fixed window, per
  authenticated identity or IP), configurable via
  `RateLimiting:PermitsPerMinute`, default 120 rpm. Rejections return 429
  with a `Retry-After` header.
- **Budget guard.** `UsageBudgetGuard` short-circuits queries once a
  tenant's daily cost or request count exceeds the configured limit,
  returning `429 Too Many Requests` (see `BudgetExceededException`).
- **Payload size.** ASP.NET Core defaults apply (default max body ~30 MB).
  Ingest endpoints do not accept files above that.

### E — Elevation of privilege

- **KnowledgeAdmin policy.** Applied on `POST /api/v1/admin/prompts`,
  `DELETE /api/v1/documents/{id}`, `POST /api/v1/eval/runs`. Enforced via
  ASP.NET authorization requiring a `role=admin` claim. Covered by the
  `ListPrompts_NonAdmin_Returns403` test.
- **Classification-based ingest guard.** An authenticated user cannot ingest
  a document at a classification level above their own
  (`Cannot ingest above your own classification level`).

---

## Prompt-injection and answer manipulation

- **Extractive answer synthesis.** The default `TemplateChatModel` does not
  ship user-controlled instructions to any LLM — it composes sentences from
  the retrieved chunks. Prompt-injection strings inside a retrieved chunk
  cannot rewrite the model's behaviour because there is no model to rewrite.
- **OpenAI adapter.** When enabled, the system prompt is fixed
  (`RagPrompts.SystemPrompt`), retrieval results are placed inside a marker
  block (`<<CTX_JSON>>...<<END_CTX_JSON>>`), and the user question is placed
  after a hard delimiter. Any adversarial chunk instructions ("ignore
  previous instructions") still receive only the retrieved content as
  context, and the grounding checker rejects unsupported output.

## Data exfiltration via retrieval

- Covered by ACL post-filter + leak tests; see ADR 0004.
- Cross-tenant queries share no state today — `Tenant` on `QueryRequest`
  scopes the budget ledger but not the vector store. Multi-tenant vector
  isolation is roadmap.

## Known non-claims

- We do **not** claim SOC 2 controls, HSM key management, or comprehensive
  audit logging.
- We do **not** run a persistent supply-chain lock file audit; `dotnet
  restore` uses the internal NuGet proxy.
- We do **not** implement mTLS, client-certificate auth or SPIFFE — the
  service assumes it is behind an authenticated gateway in production.
- The included `docker-compose.yml` (app + pgvector) is labelled
  **UNVERIFIED** at the top of the file. It is a documentation artefact
  for future work, not a supported deployment.
