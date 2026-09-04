# Local Operations Runbook

## Purpose
Operate the demonstration safely on a developer workstation. This is not a production deployment runbook; for migration operations use [migration-docs/05-cutover-runbook.md](../../migration-docs/05-cutover-runbook.md).

## Start modern API
```powershell
Set-Location C:\Users\rukwaropaul\Downloads\DEV\Projects\02-legacy-dotnet-modernization
dotnet run --project modern\src\Northstar.Api --launch-profile http
```
Expected: startup applies the local SQLite migration, seeds fictional policy data once, and listens at `http://localhost:5002`. Verify:
```powershell
Invoke-RestMethod http://localhost:5002/health/live
Invoke-RestMethod http://localhost:5002/health/ready
```

## Start legacy simulation
```powershell
dotnet run --project legacy\Northstar.Legacy.Web --launch-profile http
```
Browse `http://localhost:5102`. Do not expose it outside a local demonstration: the policyholder filter is deliberately injectable.

## Obtain a local API token
```powershell
Invoke-RestMethod http://localhost:5002/api/v1/auth/token -Method Post `
  -ContentType 'application/json' `
  -Body '{"subject":"operator","scopes":["claims:read","claims:adjust","claims:approve"]}'
```
The endpoint is unavailable in Production. Use a real OIDC provider in any non-demo deployment.

## Diagnose common failures
| Symptom | Check | Recovery |
|---|---|---|
| API refuses Production startup | Default dev signing key detected | Provide a managed non-default signing configuration outside source control |
| `409 Conflict` on assessment | Claim version is stale | Read claim, show updated state, retry with current version |
| `422` during intake | Currency/policy/domain rule failed | Correct request; do not bypass validation |
| `/health/ready` fails | SQLite path/file permission | Verify connection string and write access; restore local DB from backup if needed |
| Document rejected | MIME type/size cap failed | Use PDF/JPEG/PNG under configured max; inspect non-sensitive logs |
| Import reports rejections | Source row violates target invariant | Review report, correct source/remediation mapping, rerun dry import |

## Reset local demonstration data
Stop hosts, then delete only local ignored data (`modern\src\Northstar.Api\northstar-modern.db`, legacy database, and `App_Data` directories) if a reset is required. Never delete migration reports or backups during an actual cutover investigation.
