# Interview talking points

_Systems-design and staff/principal-level interviewing prompts._

## "Walk me through the architecture"

Modular monolith, Clean-Architecture-ish four-project layout in a single .NET solution:

- **Domain** — entities, canonical serialisation, hash chain, Merkle tree. Zero infrastructure
  dependencies. This is where correctness lives.
- **Application** — use-case services (ingest, verification, retention, export, redaction,
  reports) speak through ports. Everything is scoped.
- **Infrastructure** — EF Core adapters, the `AppendOnlyInterceptor`, RSA signing, in-memory
  tenant lock.
- **Api** — Minimal API endpoints, DI wiring, auth policies, seeding. The API is a thin shell.

Two crypto primitives (`HashChain` and `MerkleTree`) plus canonical JSON are the entire
integrity story. Everything else is plumbing.

## "How does the append-only guarantee actually work?"

Three layers.

1. **Compile-time**: private setters everywhere on `AuditEvent`. The only public mutation is
   `Tombstone(when)`.
2. **Runtime**: `AppendOnlyInterceptor` scans the change tracker on every save. `Deleted` is
   always rejected. `Modified` is rejected unless the mutation is exactly the tombstone
   transition (with a deterministic new payload) or the one-shot checkpoint signature attach.
3. **Storage**: production hardening is `REVOKE UPDATE, DELETE ON AuditEvents FROM app_role`
   at the DB layer plus WORM for archived evidence packs. Documented in ADR-005; not
   implemented on SQLite for the dev build.

I demonstrate layer 2 with a reflection test — I use reflection to force a `Modified` state
and then assert that `SaveChangesAsync` throws `DomainException(AppendOnlyViolation)`.

## "How do you reconcile retention with immutability?"

The key insight is: chain the events by their **content hash**, not by the raw payload bytes.
That decoupling is what makes tombstoning safe. When we prune:

- `PayloadJson` becomes a deterministic stub.
- `ContentHash`, `ChainHash`, `PreviousChainHash` are preserved verbatim.
- `IsTombstoned = true`.

The verifier ignores the tombstone payload and recomputes the chain link from the preserved
`ContentHash`. It matches. Chain verification still passes across pruned events. Test:
`Retention_PrunesOldEvents_AndChainStillVerifies`.

## "Why not just use QLDB or a blockchain?"

For a per-tenant, private-log use-case, both are overkill. QLDB is a specific vendor product;
blockchains are orders of magnitude more complex with no benefit here (there's no
mutually-untrusted party to coordinate). This platform implements the primitives so we have
full control over the failure semantics and the migration story. If the answer to
"cryptographically verifiable audit log" for a real regulated bank is a vendor product, that's
also fine — but this project is about being able to build one from first principles.

## "How do you handle multi-tenant chain linearity?"

Every chain write is inside an `await using` `ITenantLock` acquisition. The in-process
implementation is a `ConcurrentDictionary<string, SemaphoreSlim>`. A multi-node deployment
would swap that for an advisory lock (`pg_advisory_xact_lock` on Postgres) or a leased row.
Cross-tenant traffic parallelises freely — chains are per tenant, so scaling is per tenant.

## "Where does canonical JSON matter?"

Everywhere the hash is computed. Both sides of a comparison need to produce the same bytes.
That means: object keys sorted, no whitespace, integers as `int64` when representable, decimals
via G29-trim, NaN/Infinity rejected. I wrote a 60-line implementation with a matching test
suite because RFC 8785 is more spec than we need. I document it precisely so a verifier in
any language can reproduce it (see `docs/integrity-model.md`).

## "How would you productionise this?"

- KMS/HSM-hosted RSA private key with rotation, public-key publication, and pinned key IDs.
- Postgres with `REVOKE UPDATE, DELETE` on the audit tables.
- WORM storage (S3 Object Lock, immutable) for evidence packs and checkpoint archives.
- OTLP exporter and a real dashboard for `chain.verify.duration`, `ingest.rate`,
  `chain.length_per_tenant`, `dlq.depth`.
- Per-tenant queues for ingest to isolate a runaway tenant.
- A verifier binary that a customer can run standalone, so they don't need to trust the
  platform's `/verify` endpoint.

## "What's the biggest weakness?"

The RSA private key lives in memory in this build. That's not a design flaw — it's a
deliberate scope limit for a self-directed portfolio project. Everything else is
production-shaped; that one is dev-shaped.

The second weakness is that the search index is hand-rolled. SQLite has FTS5, and it would
be a straight upgrade.

## "How did you split work with Copilot?"

Copilot's the collaborator, not the ground truth. I designed the integrity model and the
retention/tombstone reconciliation myself. Copilot mostly filled in configuration boilerplate,
scaffolded tests around the invariants I specified, and drafted documentation that I edited
substantially. Every ADR and the security review are things I would defend without an agent
in the room.
