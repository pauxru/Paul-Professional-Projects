# Demo Script

Target length: 10–12 minutes.

## 1. Frame the problem (1 minute)

Explain that the storefront domain is intentionally small; the engineering subject is safe Azure deployment.

## 2. Show boundaries and Azure design (2 minutes)

- Open the README architecture/trust diagrams.
- Point out Container Apps revisions and migration job.
- Show managed identity role assignments and Key Vault references.
- State that no Azure resources were provisioned.

## 3. Run locally (2 minutes)

```powershell
dotnet run --project .\src\Contoso.Storefront.MigrationRunner
dotnet run --project .\src\Contoso.Storefront.Api
```

Call live/ready/startup/metrics. Create a dev token and order. Re-send the idempotency key.

## 4. Show failure properties (2 minutes)

- Open the worker cancellation test.
- Explain why publish precedes processed/checkpoint updates.
- Open readiness tests showing cache/bus/migration failure while liveness stays healthy.
- Show retry/open/half-open circuit tests.

## 5. Show migration safety (1 minute)

Open the six migration files and the expand/contract timeline. Explain why old and new revisions can coexist before contract.

## 6. Run progressive delivery simulation (2 minutes)

```powershell
.\scripts\simulate-canary.ps1 -RequestsPerStep 20
.\scripts\simulate-canary.ps1 -RequestsPerStep 30 -InjectGreenFailure
```

Point out 5/20/50/100 promotion and automatic rollback on breach.

## 7. Close with evidence/gaps (1 minute)

Show `docs/test-results.md`: build/tests/Bicep succeeded; Terraform, Docker and Azure remained unexecuted. Name the top production gaps: private DNS, PostgreSQL Entra auth, load/failover exercises.
