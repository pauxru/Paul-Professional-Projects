# ADR-004: Versioned secret references instead of value injection

Status: Accepted

## Context

Copying values into deployment manifests, tickets, environment files, or notification payloads
multiplies exposure and makes it impossible to know which version a consumer is using.

## Options

1. Inject plaintext into deployment configuration.
2. Push plaintext in rotation notifications.
3. Give consumers a versioned reference and authorize runtime resolution.

## Decision

Use option 3 with `@secret:app/environment/purpose#vN`. Notifications carry only the reference.
Consumers fetch a specific staged version, update their bounded in-memory cache, verify their own
use, and acknowledge.

## Consequences

The control plane can audit reads and reason about migration by version. Consumer applications need
a small client integration and must remain able to refresh during rotation.

## Risks

Control-plane unavailability can affect cold starts if consumers have no safe cache. An
over-permissive path policy can still expose values. References themselves reveal metadata and
should not be treated as public.

## Alternatives

Value injection was rejected because it spreads plaintext into systems not designed to protect it.
Unversioned references were rejected because they make staged migration and rollback ambiguous.
