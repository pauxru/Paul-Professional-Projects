# Security review

A durable runtime that spends money is a security artefact whether or not anyone treats it
as one. This is what was considered, what holds, and what does not.

## Threat model

The runtime executes workflows that move money and call models. The assets are: the
integrity of the effect stream (money moves once, when authorised), the integrity of the
journal (history cannot be rewritten), and the integrity of the approval gate (a human
decision cannot be forged or bypassed).

The adversary considered is **not** primarily an external attacker — there is no network
surface here. It is a workflow author making an ordinary mistake, plus a compromised or
buggy dependency of the workflow itself. That is the realistic threat for this kind of
system and it is the one the design defends against.

---

## SEC-1 — A workflow cannot choose its own idempotency key

**Status:** mitigated by construction.

The key is derived by the runtime from `runId`, effect name and ordinal. A workflow that
puts `idempotencyKey` in its payload is ignored — pinned by
`the workflow cannot supply its own key` in `tests/effects.test.ts`.

This matters because a workflow that could choose its key could *collide* with another
run's key and cause a refund to be silently deduplicated away, or *randomise* its key and
cause a duplicate. Both are one line of plausible-looking code. Removing the capability is
cheaper than reviewing for it.

## SEC-2 — Approvals cannot be forged from inside a workflow

**Status:** mitigated by construction.

`ctx.awaitSignal(name)` can only *read* signals. There is no `ctx.emitSignal`. A workflow
cannot approve itself, and the payload it receives comes from `opts.signals`, which is
supplied by the caller.

**Residual risk:** the runtime does not authenticate the signal. Whatever populates
`opts.signals` is entirely responsible for establishing that a human with authority
actually approved this specific case. If that store is writable by the same process that
runs workflows, the separation is cosmetic. In a real deployment the approval record needs
its own authorisation path and an audit trail, and the journal's `signal.received` event
should carry the approver identity — it currently carries only the payload.

## SEC-3 — History cannot be rewritten

**Status:** mitigated, with a gap.

`InMemoryJournal.append` rejects any event whose sequence is not exactly the next one.
This makes the journal append-only in practice: an event cannot be replaced, reordered or
back-dated through the public API.

`parse` stops at the first unreadable line rather than skipping it, so a corrupted record
truncates the history instead of creating a hole. Resuming past a hole would replay a
sequence of decisions that never happened, which is the more dangerous failure — a run
could skip its approval gate entirely. There is a mutant for this and it is killed.

**Residual risk:** nothing signs the journal. An actor with write access to the underlying
store can author any history they like, including one where an approval was granted. The
sequence check defends against accident and concurrency, not against tampering. A real
deployment wants a hash chain — each event carrying the digest of its predecessor — which
turns tampering into a detectable break rather than an invisible edit.

## SEC-4 — Two writers are detected but not prevented

**Status:** partially mitigated. This is the most serious gap.

If two processes resume the same run, both replay and both may reach the same effect. The
sequence-gap check makes the *second journal write* fail, so the divergence is loud — but
by then the effect may already have been issued twice. With a deduplicating gateway and
positional keys the money is safe; without one, it is not.

The fix is a lease, and it is out of scope here because it needs a store with
compare-and-swap. It is recorded in `known-limitations.md` as the largest gap between this
and a deployable system.

## SEC-5 — The unknown window is an availability/integrity trade, and it is explicit

**Status:** accepted, with the trade measured.

See ADR 005. `retry-same-key` risks a duplicate if the gateway ignores keys;
`escalate` risks a run that never completes. Both risks are quantified in §1 of the
results (1 duplicate, 1 stopped run out of 42 crash points respectively). The security
posture here is not "we solved it" but "we measured both sides and the caller chooses".

## SEC-6 — Effect payloads and journal contents are unredacted

**Status:** known, not mitigated.

The journal records every step result and every effect payload verbatim. In this project
those are amounts and case ids. In a real agent they would be prompts, model completions
and customer data — and the journal is, by design, the thing you keep and read during an
incident.

That makes the journal a data-retention surface. It needs field-level redaction (journal a
digest for anything sensitive and store the value elsewhere with its own lifecycle) before
it holds anything a regulator cares about. Nothing in the current design prevents that; it
simply is not done.

## SEC-7 — No secrets in the tree

**Status:** enforced.

Stage 6 of `test.ps1` scans for AWS keys, private key headers, Slack and GitHub tokens
across every file, and applies a stricter pattern set (`apiKey`/`clientSecret`/`password`
assignments, connection strings) to `src/` only. Production code is held to a higher
standard than fixtures deliberately: a literal named `apiKey` is legitimate in a test
fixture and is a finding in a runtime.

## SEC-8 — Supply chain

**Status:** eliminated.

Zero dependencies, enforced by stage 5 rather than asserted in a README. There is no
lockfile to drift, no transitive package to be compromised, and no install step. See ADR
003.

---

## Summary

| id | issue | status |
| --- | --- | --- |
| SEC-1 | workflow-chosen idempotency key | mitigated by construction |
| SEC-2 | self-approval | mitigated; signal authenticity is the caller's job |
| SEC-3 | history rewriting | mitigated against accident; **not** against tampering |
| SEC-4 | two writers on one run | detected, not prevented — needs a lease |
| SEC-5 | unknown-window trade | accepted, both sides measured |
| SEC-6 | unredacted journal contents | known, not mitigated |
| SEC-7 | secrets in the tree | enforced by test |
| SEC-8 | supply chain | eliminated |
