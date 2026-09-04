# Screenshots Needed

No screenshots are committed. Capture only synthetic/local or separately authorized Azure evidence.

1. README Azure target diagram rendered.
2. Terminal: migration runner applies six migrations, then idempotent second run.
3. Terminal: final `dotnet build -c Release`.
4. Terminal: final test summaries (50 unit, 16 integration).
5. Browser/terminal: live, ready and startup JSON side by side.
6. Browser/terminal: `/metrics` canary metrics.
7. Terminal: healthy canary reaches green 100%.
8. Terminal: injected failure rolls back to blue 100%.
9. Code: Container Apps Key Vault secret references and managed-identity RBAC.
10. Code: expand/backfill/contract migration sequence.

If the reference is later deployed with authorization, add Container Apps revisions/traffic, Application Insights trace correlation, Service Bus queue/DLQ and PostgreSQL HA screenshots. Do not imply those exist now.
