# Portfolio checklist / status

Snapshot of what this project ships vs. the portfolio positioning brief.

| Item                                          | Status | Where                                                                 |
|-----------------------------------------------|--------|------------------------------------------------------------------------|
| Cleanly separated Domain / App / Infra / Api  | ✅     | `src/RagAssistant.*` (7 projects)                                       |
| ADRs for load-bearing decisions               | ✅     | `docs/decisions/0001..0005`                                       |
| Security review with STRIDE + non-claims      | ✅     | `docs/security/security-review.md`                                      |
| Real evaluation metrics, not vibes            | ✅     | `docs/evaluation.md`                                                    |
| Reproducible test suite (no keys)             | ✅     | 70/70 tests pass — `docs/test-results.md`                              |
| Deterministic local AI provider by default    | ✅     | `LocalDeterministicEmbeddingModel` + `TemplateChatModel`                |
| Real OpenAI / Azure OpenAI adapters (gated)   | ✅     | `Infrastructure/Ai/OpenAi/*.cs` — behind `Ai:Provider`                  |
| Permission-aware retrieval + leak test        | ✅     | ADR 0004 + `QueryEndpointsTests.Query_RestrictedDoc_LeaksNothing`       |
| Grounding / faithfulness check                | ✅     | ADR 0005 + `GroundingCheckerTests`                                      |
| Prompt versioning                             | ✅     | `PromptService`, `AdminEndpointsTests.RegisterPrompt_AsAdmin_Succeeds`  |
| Rate limiter + budget guard                   | ✅     | `Program.cs` + `UsageBudgetGuard`                                       |
| Correlation ids + security headers            | ✅     | `Middleware/*`                                                          |
| OpenTelemetry traces + metrics                | ✅     | `Program.cs` (console exporter, meter `"RagAssistant"`)                 |
| Runbook                                       | ✅     | `docs/runbooks/rag-assistant.md`                                        |
| Portfolio pieces (5 files)                    | ✅     | this folder                                                             |
| Docker + docker-compose (UNVERIFIED)          | ⚠️     | Shipped as reference artefacts, labelled unverified                    |
| GitHub Actions CI                             | ✅     | `.github/workflows/ci.yml`                                              |
| Demo script                                   | ✅     | `docs/portfolio/demo-script.md` + `scripts/demo.ps1`                    |

## Known gaps / roadmap

- **pgvector adapter.** Documented in ADR 0003; not implemented. Brute-force
  cosine covers the demo corpus size cleanly.
- **EF Core migrations.** Schema is created via `EnsureCreatedAsync`.
- **NLI-based grounding second layer.** Lexical support is the current
  first layer; NLI is a natural upgrade once a real LLM is enabled.
- **Cross-tenant vector isolation.** `Tenant` scopes the budget ledger
  today, not the store.
