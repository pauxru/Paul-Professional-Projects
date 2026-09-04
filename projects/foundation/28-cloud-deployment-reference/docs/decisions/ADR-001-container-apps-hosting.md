# ADR-001 — Use Azure Container Apps for the Default Runtime

## Context

The storefront needs revision traffic splitting, autoscaling, managed ingress, jobs for migrations and a lower operations burden than a general Kubernetes platform.

## Options

1. Azure Container Apps.
2. Azure Kubernetes Service.
3. Azure App Service.

## Decision

Use Container Apps for the API/worker revision and a Container Apps Job for migrations. Keep the application Kubernetes-friendly through HTTP probes, stateless API replicas, external persistence and graceful termination.

## Consequences

- Revision mode supports blue/green and canary traffic.
- KEDA-backed scaling and managed ingress reduce platform work.
- Operators use Container Apps concepts rather than direct Kubernetes objects.
- The worker initially scales with the API because both run in one process.

## Risks

- Some advanced networking, sidecar and scheduling needs may exceed Container Apps capabilities.
- Coupled API/worker scaling can waste capacity or constrain queue throughput.
- Resource-provider behaviour was not deployment-tested in this case study.

## Alternatives

AKS is preferred when the organization already operates Kubernetes or needs custom controllers, service mesh, privileged workloads or advanced scheduling. App Service is reasonable for a simpler HTTP-only workload but is less natural for revision traffic and the migration job pattern.
