# Secret Rotation Playbook

## Universal pre-flight

1. Confirm the registry path, owner, environment, criticality, consumers, rotation interval, max
   age, and grace period.
2. Confirm each consumer resolves `@secret:app/env/purpose#vN` at runtime and does not embed values.
3. Prefer dual-write. Use single-cutover only when the target cannot accept overlapping values.
4. Set a maintenance window for single-cutover.
5. Confirm verification is specific enough to detect an unusable credential without mutating data.
6. Watch notification delivery and consumer acknowledgements.
7. Do not expose the generated value in tickets, terminals, logs, screenshots, or chat.
8. Retire the previous version only after the grace period and evidence review.

## Rotation state interpretation

| State | Operator meaning |
|---|---|
| `Requested` | Durable intent exists |
| `Generating` | New material is being generated |
| `StagedNewVersion` | Encrypted candidate exists, not current |
| `NotifyingConsumers` | Reference-only notifications are being sent |
| `AwaitingAcknowledgement` | Consumers must refresh, or the window must begin |
| `Promoting` | Promotion intent is persisted |
| `Verifying` | Candidate is checked before actual activation |
| `Completed` | Candidate is current; old current is previous |
| `RolledBack` | Candidate is revoked; known-good current remains/restores |
| `Failed` | Unexpected internal step failed; inspect and resume or rollback |

## Type-specific procedures

### API key

- Create the new provider-side key while the old key remains accepted.
- Stage and distribute the new reference; require consumer refresh acknowledgement.
- Verify with a read-only authenticated API request, then promote and later disable the old key.
- **What breaks if you get it wrong:** immediate 401/403 errors, retry storms, and locked-out
  integrations.

### Database password

- Create or alter credentials so both passwords/users are valid where the database supports it.
- Refresh connection pools after consumers load the staged reference.
- Verify with a connection plus a harmless `SELECT 1`.
- **What breaks if you get it wrong:** pooled connections mask failure until reconnect; all new
  connections can fail simultaneously and cause an outage.

### Service account key

- Add a new RSA public key/key ID to the service account before distributing the private half.
- Verify a signed challenge with the registered public key.
- Remove the previous public key only after the grace period.
- **What breaks if you get it wrong:** workloads cannot authenticate; deleting the old key early
  can strand offline or slowly deployed instances.

### Signing key

- Publish the new ECDSA public key and key ID before any token is signed by it.
- Keep the previous verification key available until every token it signed has expired.
- Verify a sign/verify round trip before promotion.
- **What breaks if you get it wrong:** newly issued tokens are rejected, or old valid tokens become
  unverifiable.

### Connection string

- Treat the embedded credential and endpoint/options as one atomic value.
- Validate DNS/TLS settings and open a test connection before promotion.
- Drain or recycle pools deliberately.
- **What breaks if you get it wrong:** subtle option loss can disable encryption, point at the
  wrong database, or trigger a full connection outage.

### Certificate

- Install the full chain and trust anchors before switching bindings.
- Confirm subject/SAN, EKU, validity window, and private-key availability.
- Allow overlap longer than the longest client cache.
- **What breaks if you get it wrong:** TLS handshakes fail, clients reject names/chains, or a node
  starts before `NotBefore`.

### Webhook signing secret

- Configure verifiers to accept old and new HMAC keys during the transition.
- Verify an exact-body signature and replay checks before promotion.
- **What breaks if you get it wrong:** legitimate events are dropped or spoofed messages are
  accepted.

### Encryption key

- Distinguish wrapping-key rotation from data-key rotation.
- For wrapping-key rotation, re-wrap DEKs without decrypting values.
- For a data-key rotation, re-encrypt in bounded batches and retain recovery metadata.
- **What breaks if you get it wrong:** irreversible data loss can occur if the old key is destroyed
  before every ciphertext is migrated and verified.

## Rollback decision

Rollback immediately for failed verification, missed acknowledgement deadline, unexpected
authentication failures, elevated error rate, or unclear ownership. Do not “push through” by
revoking the old value. Follow [failed-rotation.md](runbooks/failed-rotation.md).

## Retirement and destruction

After completion, keep the previous version only through the documented grace period. Revocation
is reversible only through a new rotation; destruction clears encrypted material and requires an
approved, single-use four-eyes request. Preserve lifecycle and audit metadata.
