# Demo Script

Target duration: 10–12 minutes.

## 1. Frame the system

“This is an IGA governance plane, not an identity provider. An external OIDC system would authenticate administrators; this service decides desired access, orchestrates approval/provisioning, reconciles drift, and retains evidence.”

Show the architecture diagram and the 500 synthetic identities / 25 applications.

## 2. Explain access

Open the user directory, choose a Technology user, and click **Why access?**. Point out:

- dynamic group membership;
- group-assigned role;
- nested inherited roles;
- final entitlement;
- more than one path retained when present.

## 3. Run a decision

Use the simulator/evaluation API with network zone, device trust, MFA, resource classification, and cost centre. Show every policy evaluated and explain that a deny wins even when a role grants the permission.

## 4. Lifecycle

Run `scripts/demo.ps1`. Narrate:

- create a pending Finance joiner;
- activate, calculate birthright access, and provision;
- request access and traverse manager/owner/security as applicable;
- create JIT privileged access and show decision changing;
- wait for expiry and show decision returning to deny.

## 5. Certification

Create a short application campaign, inspect the derivation snapshot, let the deadline elapse, invoke deadline enforcement, and show:

- item becomes auto-revoked;
- campaign becomes overdue;
- authorization no longer resolves the entitlement.

## 6. Drift

Explain that the target connector state is independent. Run reconciliation and show how an account without an active source becomes an orphan and an unapproved target permission becomes a rogue grant.

## 7. Reliability and security

Show:

- retry attempt count and quarantine model;
- fake-clock tests for JIT/campaign expiry;
- approver ID bound to JWT subject;
- append-only audit guard and hashes;
- production default-key startup refusal.

## 8. Close

State trade-offs honestly: SQLite/`EnsureCreated`, in-memory targets, development-only token issuer, synchronous simulation, no Docker verification, and no production/compliance claim.
