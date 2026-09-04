# ADR-001: Envelope encryption with metadata-bound AAD

Status: Accepted

## Context

The control plane must store many independently rotatable secret versions, detect tampering, limit
the impact of a single data-key exposure, and rotate its master key without decrypting every value.
Moving a valid ciphertext row to another registry record must fail.

## Options

1. Encrypt every value directly with one master key.
2. Envelope-encrypt each version with a unique DEK but omit associated data.
3. Envelope-encrypt with a unique DEK and bind identity metadata as AES-GCM AAD.

## Decision

Use option 3. Each version gets a CSPRNG-generated 256-bit DEK. AES-256-GCM authenticates the
canonical tuple `secret-id | path | type | version`. `IKeyProvider` wraps the DEK and records the
wrapping-key version.

## Consequences

Master-key rotation re-wraps small DEKs while ciphertext, nonce, and tag remain unchanged.
Tampering or row transplant fails authentication. The application must preserve canonical metadata
and historical wrapping-key versions.

## Risks

Nonce reuse under one DEK would be catastrophic; unique DEKs and random 96-bit nonces reduce this
risk. Process compromise can still observe plaintext and key material. Losing an old wrapping key
before re-wrap completes makes versions unreadable.

## Alternatives

Direct master-key encryption was rejected because it makes rotation expensive and broadens key
reuse. Encryption without AAD was rejected because it cannot detect valid-ciphertext transplant.
