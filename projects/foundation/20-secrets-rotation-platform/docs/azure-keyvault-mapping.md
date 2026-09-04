# Azure Key Vault Mapping

## Status

**No Azure resources were provisioned.** The repository contains a compile-checked adapter shape,
`AzureKeyVaultExternalSecretStore`, over the local `IAzureKeyVaultClient` seam. The default client
throws a clear `NotSupportedException`; it never attempts network access.

## Concept mapping

| Northstar concept | Azure Key Vault concept |
|---|---|
| Hierarchical `orders/prod/database` | Secret name `orders--prod--database` |
| Northstar version number | Tag `northstar-version`; Key Vault returns its own version ID |
| Owner/environment/type | Tags |
| Encrypted local version | Key Vault secret value when external-store mode is selected |
| Disable/revoke | Disable Key Vault secret version |
| Master wrapping key | Key Vault key or Managed HSM key using wrap/unwrap operations |
| Workload permission | Managed identity + RBAC role scoped to vault/key |

## SDK implementation shape

A production adapter would implement `IAzureKeyVaultClient` with Azure SDK clients:

- `SecretClient.SetSecretAsync` / `GetSecretAsync` / `UpdateSecretPropertiesAsync`;
- `CryptographyClient.WrapKeyAsync` and `UnwrapKeyAsync` for `IKeyProvider`;
- `DefaultAzureCredential` or workload identity, never a checked-in client secret;
- private endpoint and firewall policy;
- retry/timeout policy with Azure error classification;
- Key Vault diagnostic logs routed to the security monitoring account.

The Northstar version must be persisted alongside the Azure-returned version identifier. Reads
must request a specific version, and disabling an old Key Vault version must occur only after the
consumer grace period.

## Production guardrails

1. Separate vaults and identities by environment and blast radius.
2. Grant wrap/unwrap separately from secret get/set where practical.
3. Disable purge protection only under an approved disaster-recovery design; normally enable soft
   delete and purge protection.
4. Export control-plane audit logs to immutable storage.
5. Test throttling, regional outage, disabled identity, deleted key version, and recovery.
6. Never enable the local development master-key fallback outside Development.

## Non-claims

This mapping demonstrates architecture and compile-time boundaries only. It does not claim Azure
deployment, Azure validation, Managed HSM protection, private networking, or production readiness.
