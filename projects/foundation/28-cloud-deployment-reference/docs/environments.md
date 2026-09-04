# Environments

All figures are starting points, not measured production recommendations.

| Setting | Dev | Staging | Prod |
|---|---:|---:|---:|
| API replicas | 1–3 | 1–5 | 3–20 |
| PostgreSQL | Burstable B1ms, 32 GB | GP D2s v3, 64 GB | GP D4s v3, 256 GB |
| PostgreSQL zone HA | No | No | Yes |
| Redis | Basic C0 | Standard C1 | Premium P1 |
| Service Bus | Standard | Standard | Premium |
| Private access | Optional/off | On | On |
| ACR | Basic | Basic | Premium |
| Log retention | 30 days | 30 days | 90 days |
| Approval | Team auto/manual policy | Manual environment approval | Manual protected environment approval |

## Required tags

Every Bicep and Terraform environment defines:

- `application`
- `environment`
- `owner`
- `managed-by`
- `data-classification`

Unit tests parse both parameter formats and fail if required tags or production invariants disappear.

## Configuration promotion

Promote the same immutable image digest. Environment differences belong in IaC parameters, Key Vault and GitHub Environment variables, not rebuilt binaries. Feature flags may differ while code remains identical.

## Secret handling

`POSTGRES_ADMIN_PASSWORD` and `JWT_SIGNING_KEY` are secure runtime inputs to Bicep, or `TF_VAR_postgres_administrator_password` and `TF_VAR_jwt_signing_key` for Terraform. They are never written to an environment file. Azure login identifiers are non-secret GitHub Environment variables.
