# Demonstration Script

Target length: 8–10 minutes.

## 1. Frame the problem (60 seconds)

Explain that field-operation CRUD is straightforward; the engineering subject is preventing tenant data crossover while implementing the commercial controls a SaaS business needs.

## 2. Show architecture (60 seconds)

Open the README container diagram. Point out the dependency rule and the four isolation boundaries: resolution, policy/membership, application guard, EF filter/interceptor.

## 3. Run the system (60 seconds)

```powershell
dotnet run --project src\FieldOps.Api --urls http://localhost:5004
.\scripts\demo.ps1
```

Open the dashboard and connect as the Savanna owner.

## 4. Prove isolation (2 minutes)

- List Jua Kali jobs and capture one ID.
- Request that ID with Savanna credentials.
- Show the 404 rather than a revealing 403.
- Open `CrossTenantIsolationTests` and point to the list, update and forgotten-filter tests.
- Explain that the interceptor compares original and current tenant IDs.

## 5. Show commercial machinery (2 minutes)

- Open usage/plan cards.
- Toggle a feature flag and show audit evidence.
- Explain deterministic `(flagKey,userId)` rollout and kill-switch priority.
- Generate a signed payment failure, replay it, and show that only the first delivery advances dunning.

## 6. Show live RBAC (60 seconds)

Connect as Viewer and attempt an asset creation (403). Explain that the handler reads current membership, while cache invalidation makes role changes effective without process restart.

## 7. Verification and trade-offs (90 seconds)

Run:

```powershell
dotnet build -c Release
dotnet test -c Release
```

Discuss SQLite/process-local quota trade-offs and the production path: OIDC, managed relational storage with row-level security, distributed atomic meters/cache, cloud object storage and outbox/reconciliation.
