# Security Review — Industrial IoT Monitoring Platform

## Scope and method
This review is a design-level STRIDE review of the simulator, hand-written MQTT 3.1.1 broker/client, edge gateway, HTTP API, SQLite stores, dashboard, command path, and firmware example. It is based on code inspection and automated tests in this repository; it is not a security assessment of a deployed system.

## Assets
- Device identifiers, hierarchy, firmware versions, twin desired/reported properties, and synthetic telemetry.
- Per-device keys, enrolment tokens, simulated certificate thumbprints, JWT signing configuration, and broker connection credentials.
- Commands, command audit entries, alert state, OTA desired versions, and durable edge queue records.
- Availability of gateway buffering, ingestion, alerting, and command delivery.

## Trust boundaries
1. Device/firmware ↔ local MQTT transport.
2. Local broker ↔ edge gateway subscription and command relay.
3. Edge gateway ↔ cloud HTTP ingestion.
4. Browser/operator ↔ JWT-protected API/dashboard.
5. API/application ↔ SQLite files and audit log.
6. OTA desired state ↔ device-side download/verify/apply simulation.

## Data classification
All seeded values are synthetic and Jua Kali Manufacturing Ltd is fictional. Device keys and JWT signing keys are secrets; telemetry and hierarchy are operationally sensitive in a real plant; audit records are integrity-sensitive. No personal data, real credentials, or real firmware binary is included.

## Threat model (STRIDE per boundary)

| Boundary | S | T | R | I | D | E | Implemented controls / residual risk |
|---|---|---|---|---|---|---|---|
| Device ↔ MQTT | Device impersonation | forged topics/payload | denied publish | clear-text telemetry | packet flood | subscribe outside role | Codec validates packet structure, topic filters and QoS 0/1; local demo broker has no TLS or persistent ACLs. Production requires mTLS and broker ACLs. |
| Broker ↔ edge | rogue local publisher | retained-message poisoning | missing trace | broad wildcard read | subscription flood | command topic abuse | `IMessageTransport` enables replacement; edge command relay is typed at device layer. Broker authentication callback exists, but production authorization is not claimed. |
| Edge ↔ API | device key theft | altered gzip body | replay claims | key/header leakage | ingest flood | cross-device ingest | PBKDF2 hashes, claimed ID/key binding, revocation, unique sequence key, rate limiter, ProblemDetails. No TLS is configured for local localhost demo. |
| Operator ↔ API | stolen JWT | twin/command tamper | operator denial | dashboard token disclosure | UI/API flood | operator becomes admin | JWT issuer/audience/signature/lifetime validation and scope policies; development token route is disabled in Production. |
| API ↔ SQLite | process identity abuse | database/file modification | changed audit row | file disclosure | database lock | direct DB write | parameterized EF Core, indexes, append-only application audit behavior. Filesystem ACLs and encrypted-at-rest storage are deployment responsibilities. |
| OTA path | spoofed firmware request | malicious binary | false update completion | version leakage | update loop | arbitrary command execution | strict `firmwareUpdate` type and desired twin version; simulator verifies failure names only. Signed manifests, hashes, secure boot, and rollback counters are production work. |

## Mitigations implemented
- Per-device keys and enrolment tokens are salted PBKDF2-SHA256 hashes; comparison uses `CryptographicOperations.FixedTimeEquals`.
- Revocation blocks device authentication. Simulated X.509 thumbprints are persisted as metadata but do not claim certificate validation.
- Device key ingest requires `X-Device-Id` to match every batch reading's `deviceId`; unique `(device_id, sequence)` prevents replay duplication.
- MQTT codec rejects malformed remaining lengths, invalid fixed flags, invalid QoS, malformed UTF-8, zero packet IDs, and invalid filter syntax. The broker supports bounded required packet types only.
- Commands use a strict per-device-type allow-list and typed parameter ranges. No generic shell/script payload is available.
- Versioned desired/reported twin patches prevent silent last-writer-wins updates.
- JWT scopes distinguish admin/operator paths; production startup rejects the default development signing key.
- Rate limiting partitions ingest by device header; error output uses ProblemDetails; correlation IDs and security headers are emitted.
- Edge storage is bounded and failure handling is tested; backup queues do not grow without limit.

## Residual risk
The demonstration broker is loopback-oriented and lacks TLS, mTLS, ACL persistence, QoS 2, offline session retransmission, and denial-of-service hardening. Browser dashboard token issuance is development-only and is not an authentication product. SQLite files are not encrypted. Device software credentials and simulated OTA verification are not hardware-rooted. Statistical alerts are advisory and must never be the only safety control.

## What would change for a real production deployment
Use manufacturing-issued certificates in a secure element; mTLS to a hardened managed broker with per-topic ACLs; device certificate/key rotation and revocation distribution; encrypted secret storage; signed firmware manifests plus secure boot/anti-rollback; network segmentation; OIDC/JWKS and MFA for operators; durable immutable audit export; vulnerability management; load/DDOS testing; database backup/HA; and independent threat modelling/penetration testing.

## Explicit non-claims
This is a self-directed engineering demonstration. No formal security audit, penetration test, or compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or is claimed. It does not claim to be safe for controlling physical equipment or to meet any industrial safety standard.
