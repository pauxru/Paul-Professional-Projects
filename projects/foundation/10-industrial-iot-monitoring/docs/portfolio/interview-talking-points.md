# Interview Talking Points

## Why not microservices?
The application is deliberately a modular monolith: local execution and traceable recovery logic matter more than inventing deployment complexity. The edge/cloud idempotency boundary is still explicit.

## What fails at 3am?
WAN loss, device disconnect, malformed MQTT frames, duplicate retry, full edge storage, flapping thresholds, stale twins, unsafe commands, and OTA verification failure. Each has a bounded behavior and a test/runbook.

## What is the differentiator?
The MQTT 3.1.1 codec implements variable-length remaining-length framing, strict packet validation, retained/LWT/QoS1 behavior, and receives loopback TCP integration tests. It is intentionally scoped rather than a claim to replace a hardened broker.

## How does recovery work?
The edge never deletes a buffered reading until the cloud has accepted it or identified it as a duplicate. The cloud's unique `(deviceId, sequence)` key makes at-least-once replay exactly-once-effective.

## What changes in enterprise deployment?
Managed broker plus mTLS/ACLs, secret manager/HSM, signed firmware, OIDC/JWKS, multi-node data storage, outbox processing, real device validation, and safety engineering would be added.
