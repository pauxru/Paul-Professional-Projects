# Security review

A review of this codebase as though it were going to production, written by the person
who wrote it. That is the wrong reviewer, and the finding list below is shorter than a
real review would produce. What it does do is state the threat model precisely enough
that someone else can check whether it is the right one.

## What is being protected

Three authentication paths that all terminate in the same `CanonicalPrincipal`. The
assets are: the ability to authenticate as a given subject, the scopes attached to that
subject, and the stored password material.

## Threat model

**In scope.** An attacker who can (a) reach the login endpoint, (b) time responses, (c)
present tokens, tickets and assertions of their choosing, and (d) has obtained a copy of
the password store. Also an attacker who is a legitimate low-privilege user.

**Out of scope.** Physical access, a compromised host, a malicious insider with write
access to the directory, and anything requiring the RSA private key or the Forms
validation key — those are assumed secret and their management is explicitly out of scope
(see `known-limitations.md`).

---

## Findings

### SEC-1 — An account is only as secure as the weakest enabled stack. **By design; must be scheduled out.**

Because every enabled path produces the same principal, a user who has moved to OIDC with
a fresh Argon2id hash is still reachable through a forged Forms ticket while that path is
open. Measured in §4 of `docs/results.md`: reachable during coexistence, not reachable
after the cutoff.

This is not a defect in the bridge; it is the definition of coexistence. The mitigation
is not code — it is the cutoff date in ADR 003, and the fact that the date cannot be
reached by waiting (§3: 9,454 of 20,000 sessions still valid after 180 days) is the
security argument for setting one.

**Status:** accepted, with a required control (a scheduled, enforced cutoff plus key
rotation).

### SEC-2 — Password-format enumeration via response timing. **Mitigated.**

A 611x spread between the slowest and fastest stored format allows four-way format
identification at 98.3% accuracy from a *single* sample, which enumerates exactly the
accounts still on salted SHA-1.

Mitigated by padding verification to a constant deadline: 28.3% four-way against a 25%
chance baseline, and 66.7% binary against a 75% base rate — i.e. worse than guessing. See
ADR 004.

**Residual risk:** the padding is only active while `HashPolicy` says so. A deployment
that disables it for capacity reasons on day 1 — when the overhead is 5.55x and the
temptation is highest — reopens the channel at its widest.

### SEC-3 — Padding oracle in the legacy ticket protector. **Present on purpose; isolated.**

`LegacyTicketProtector` distinguishes `BadPadding`, `BadMac` and `Malformed`. Three
distinguishable reasons over four tampered tickets is a decryption oracle.
`HardenedTicketProtector` returns `BadMac` for everything including non-hex input, which
is why the equivalent measurement gives one reason.

**Status:** the legacy class is a demonstration artefact. `known-limitations.md` says so
in bold. A test asserts the hardened class's uniform behaviour so that "fixing" the
apparent bug fails the build.

### SEC-4 — A missing `exp` claim was accepted. **Found by tests; fixed.**

`JwtCodec.Validate` was written as:

```csharp
if (claims["exp"] is { } exp && seconds >= exp.GetValue<long>())
```

which reads as a null guard and behaves as an opt-out: omit the claim, and the token
never expires. Now absence returns `JwtFailure.MissingExpiry`. A mutation in `test.ps1`
reverts it, and the suite kills it.

This is the most instructive finding in the project because the bug is *shaped like
defensive code*. Pattern-matching null guards are how you write careful C#, and here
careful C# produced a permanent token.

### SEC-5 — XML signature wrapping. **Structurally excluded.**

The WS-Federation implementation accepts one fixed assertion shape, verifies the
signature over the exact bytes received before interpreting any of them as structure, and
never resolves a reference by `ID`. Duplicate-ID wrapping, wrapped-original, comment
injection and unsigned substitution are all rejected — 17 of 17 token and assertion
attacks in §5.

**Cost:** it will not interoperate with a real STS. ADR 006.

### SEC-6 — Deprovisioning does not take effect on the legacy paths. **Open; documented.**

`FromFormsTicket` and `FromWsFederation` take roles from the credential and never consult
the directory, so a ticket for a deleted user authenticates. `FromIdToken` refuses.

The principal gets `Tenant = "unknown"`, so tenant-scoped resources fail closed — a
partial mitigation, not a fix. Adding the directory check is one line and is a behaviour
change that will lock out anyone whose directory row is missing for an unrelated reason.

**Status:** open, deliberately. Pinned by `SessionBridgeTests` so it cannot change by
accident, and recorded in `known-limitations.md` so it cannot persist by inattention.

### SEC-7 — Authorization escalation through the naive claims transformation. **Fixed.**

592 of 4096 decisions diverged from the legacy policy under a role-to-scope table, and
**all 592 were escalations**. The deny channel (ADR 002) takes it to 16; correcting one
wrong grant row takes it to 0.

The security-relevant observation is the failure direction. All-escalation means the
defect is invisible to UAT: users report doors that should be open and are shut, never
the reverse. Any migration of an authorization model should measure the *direction* of
its divergences, not just the count.

### SEC-8 — Argon2 parameter changes silently invalidate stored hashes. **Documented.**

RFC 9106 computes `m' = 4p·floor(m/4p)`, but H0 commits to the **requested** m. So m=8
and m=11 at p=1 fill identical memory and produce different tags. An operator who
"increases" memory from 8 to 11 MiB gains no security and invalidates every stored hash —
presenting as a fleet-wide login failure with no error in any log.

**Status:** pinned by a test in `Argon2Tests`, which is the only place this is likely to
be noticed before it happens.

---

## Not reviewed

- Key management, rotation and storage (out of scope by assumption).
- Concurrency: no review of lock contention or the rehash thundering herd.
- Denial of service: Argon2id at 64 MiB × p=4 is 256 MiB of allocation per verification,
  and nothing here rate-limits.
- The `UserDirectory`, which is an in-memory dictionary standing in for a database.
- Anything about the transport. There is no transport.
