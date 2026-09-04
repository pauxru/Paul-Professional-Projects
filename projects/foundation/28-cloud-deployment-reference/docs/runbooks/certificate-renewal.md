# Runbook — Certificate Renewal

## Scope

Container Apps managed certificates or a customer-managed certificate referenced from Key Vault. No certificate was provisioned by this repository.

## Managed certificate

1. Check custom-domain validation/DNS.
2. Verify certificate status and expiry in Container Apps.
3. Resolve DNS ownership or issuance errors before expiry.
4. Test HTTPS, hostname and full chain from an external probe.

```powershell
az containerapp hostname list -g RESOURCE_GROUP -n APP_NAME --output table
az containerapp env certificate list -g RESOURCE_GROUP -n ENVIRONMENT_NAME --output table
```

Confirm exact CLI fields against the installed extension/version.

## Key Vault certificate

1. Import a new certificate version through the approved PKI process.
2. Verify Key Vault RBAC and certificate chain.
3. Update the Container Apps environment binding if it pins a version.
4. Create/warm a candidate revision if application settings changed.
5. Test SNI hostname, chain, expiry and TLS policy.
6. Keep the previous version through the rollback window.

Never commit or print PFX bytes/passwords.

## Alerts and timing

- Alert at 45, 30, 14 and 7 days.
- Complete renewal at least 14 days before expiry.
- Treat failed renewal inside seven days as an incident.

## Verification

Use an external TLS checker approved by the organization and record serial number, issuer, not-before/not-after, domain binding and change ticket. This case study has no deployed domain to verify.
