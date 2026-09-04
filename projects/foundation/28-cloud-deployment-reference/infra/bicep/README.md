# Bicep Reference

`main.bicep` runs at subscription scope, creates an environment resource group and composes modules for network, observability, ACR, identity/RBAC, Key Vault/secrets, PostgreSQL, Redis, Service Bus, private endpoints and Container Apps/API/migration job.

Environment inputs are in `environments/*.bicepparam`. Secure parameters are read from process environment variables and are never stored in those files.

Local syntax evidence:

```powershell
az bicep build --file .\infra\bicep\main.bicep
az bicep build-params --file .\infra\bicep\environments\dev.bicepparam
```

Bicep CLI 0.44.1 completed the main build and all three parameter builds successfully. That is compilation only: no What-If, policy evaluation or Azure deployment ran.
