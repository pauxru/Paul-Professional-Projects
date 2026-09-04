# Relevance Evaluation

## Purpose
This is a reproducible synthetic relevance harness, not a claim about production relevance. It protects the local engine's retrieval contracts while exercising all 30 golden document/query pairs in the seeded fictional Contoso Retail catalogue.

## Golden set
`GoldenEvaluationSet.Create()` contains 30 distinct query tokens (`qtoken01` through `qtoken30`). Each has one judged fictional catalogue document (`catalogue-golden-01` through `catalogue-golden-30`) at grade 3. The corpus also contains 4,970 non-golden catalogue documents plus 1,000 support documents. The intentionally controlled labels make this a regression fixture for analysis, ranking, vector fusion, and aliases; it is not a substitute for human-labelled customer search data.

## Exact command
Executed from the repository root on 2026-09-03 using Windows, .NET SDK 10.0.400 / .NET runtime 10.0.11:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Logging__LogLevel__Microsoft_EntityFrameworkCore = "Warning"
$env:Logging__LogLevel__Default = "Warning"
dotnet run --project src\EnterpriseSearch.Api\EnterpriseSearch.Api.csproj -c Release --no-build -- --evaluate
```

The command initializes/hydrates the SQLite logical index snapshot, seeds the fictional corpus if it is absent, runs `/api/v1/eval/run`'s same harness, prints JSON, and exits.

## Measured output

| Retrieval mode | nDCG@10 | MRR | Precision@10 | Recall@10 |
|---|---:|---:|---:|---:|
| Keyword BM25 | 1.000000 | 1.000000 | 0.100000 | 1.000000 |
| Vector (exact deterministic cosine) | 0.286696 | 0.214339 | 0.053333 | 0.533333 |
| Hybrid weighted RRF (lexical weight 2, vector weight 1) | 1.000000 | 1.000000 | 0.100000 | 1.000000 |
| Hybrid normalized linear (0.6 lexical / 0.4 vector) | 1.000000 | 1.000000 | 0.100000 | 1.000000 |

Precision@10 is 0.1 when the one relevant document appears in the top ten; that denominator is deliberately fixed at 10. The lexical-biased RRF and linear hybrid tie the lexical baseline on this controlled set. Vector-only retrieval loses recall because the local hashed embedding has collisions and is intentionally not a network semantic model.

## Regression assertion
`EvaluationHarness_HybridNdcg_IsAtLeastKeywordOnSeededGoldenSet` seeds the complete fictional corpus and asserts that weighted RRF nDCG@10 is at least keyword nDCG@10 across all 30 golden queries. It is a guardrail for modifications to analyzers, fusion, and candidate selection.

## Interpreting and improving results
For a real deployment, collect consented, de-identified click/judgement samples, define intents and segment metrics (catalogue vs support, ACL scope, language, zero-result queries), freeze a held-out test set, and tune analyzers/vector model/fusion weights without using the held-out set. Report confidence intervals and inspect query-level failures rather than treating this synthetic score as a business outcome.
