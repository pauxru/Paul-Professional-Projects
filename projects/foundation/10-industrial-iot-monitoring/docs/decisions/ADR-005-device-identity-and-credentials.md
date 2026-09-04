# ADR-005: Device keys, simulated thumbprints, and twin versioning

## Context
The system must distinguish device ingestion from human administration while remaining runnable without a PKI, secret manager, or hardware security module.

## Options
1. Use shared unauthenticated MQTT and HTTP credentials.
2. Persist plaintext per-device keys.
3. Persist salted PBKDF2 key/token hashes, simulate X.509 thumbprints, enforce revocation, and version twin patches.

## Decision
Choose option 3. A provisioning response exposes a generated/demo device key once; persistence stores only its PBKDF2-SHA256 hash. HTTP ingestion binds the key to its claimed device ID. Enrolment token hashes and revocation are checked, thumbprints are metadata in this local demonstration, and desired/reported twin patches require the expected version.

## Consequences
The model makes impersonation, replay boundaries, and concurrent twin updates explicit without pretending to provide hardware-backed identity.

## Risks
Keys remain software credentials in a development demonstration, and the TCP demo broker has no TLS/mutual certificate verification.

## Alternatives
Production deployment should use manufacturing provisioning, secure elements, mutually authenticated TLS, certificate rotation, an HSM/key vault, signed firmware manifests, broker topic ACLs, and OIDC workload identity for services.
