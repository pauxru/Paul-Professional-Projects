# Upwork Description

**Azure cloud deployment reference architecture — self-directed engineering case study**

Problem: A fictional retail API needed a credible deployment path covering identity, migrations, health, observability and rollback—not just an infrastructure diagram.

Built: A .NET 10 storefront API with transactional outbox/worker, dedicated migration runner, SQLite local execution and Azure-targeted Bicep/Terraform for Container Apps, PostgreSQL, Redis, Service Bus, Key Vault, ACR and Application Insights.

Engineering focus: managed identity and Key Vault references, OIDC GitHub delivery, startup/readiness/liveness semantics, graceful draining, checkpoint-safe cancellation, expand/contract migrations, progressive traffic gates and automatic rollback.

Stack: ASP.NET Core, EF Core, OpenTelemetry, Azure Container Apps, Bicep, Terraform, GitHub Actions.

Verification: Release build passed; 66 tests passed; Bicep main/parameters compiled; local canary promotion and rollback ran. Terraform, Docker and Azure deployment were not executed.

This is a self-directed portfolio project, not client work.
