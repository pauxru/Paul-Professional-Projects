# 001. Three authentication stacks run at once, behind one principal type

**Status:** accepted
**Date:** 2026-02-10

## Context

The system being modernised authenticates three ways, and all three are load-bearing:

- **ASP.NET Forms authentication.** An encrypted, MACed cookie carrying the user's name
  and a pipe-separated role list. Sliding expiry, 30 days. Roughly 60% of traffic.
- **WS-Federation.** A SAML-ish assertion from an on-premises STS, used by two acquired
  business units whose desktops are domain-joined to a forest we do not control.
- **OIDC.** The target. Nothing uses it yet.

The instinct is to pick a cutover date and move everyone. That instinct has failed here
twice already, and the reason it failed is not political. It is that the Forms cookie has
a 30-day sliding expiry, so the population holding a valid legacy credential does not go
to zero on any date you choose — it decays, and it decays from a base that is being
refreshed every time somebody logs in. Section 3 of `docs/results.md` measures this:
9,454 of 20,000 sessions are still valid **180 days** after the cutoff announcement.

So the three stacks will coexist. The question is what shape the coexistence takes.

## Decision

Every enabled stack terminates in the same type: `CanonicalPrincipal`. Application code
never sees a `FormsTicket`, a SAML assertion or a JWT. It sees a subject, a set of
scopes, a set of *deny* scopes, a tenant, and an authentication timestamp.

`SessionBridge` owns the conversion. Switching a stack off is a change to a
`StackConfiguration` record — a value, not a deployment — so every intermediate state of
the migration is something a test can construct.

## Consequences

**Good.** The rollback plan stops being a document and becomes a constructor argument.
`StackConfiguration.Before`, `.Coexistence` and `.After` are all exercised by the suite,
which means the configuration we would reach for at 3am under pressure is the one that
has been tested most.

**Bad, and it is the point.** Because every enabled path produces the *same* principal,
an account's real security is the security of the **weakest enabled path**, not the one
its owner uses. Section 4 of the report demonstrates this concretely: a user who has
moved to OIDC and has a fresh Argon2id hash is still reachable through a forged Forms
ticket for as long as that path is open. The Argon2id hash is irrelevant to the attack.

That property is not created by this design — it is created by running three stacks. What
this design does is make it *visible*, in one class, with a test that fails if it ever
stops being true. The alternative architectures hide the same property across three
middleware pipelines where nobody can see it at all.

**Also bad.** The canonical principal is a lowest common denominator. WS-Federation
assertions carry claims that Forms tickets cannot express; those claims are dropped.
This was acceptable because nothing downstream read them, which we established by
grepping, not by reasoning.

## Alternatives considered

**Claims transformation middleware per stack, no shared type.** Rejected: it produces
three subtly different notions of "the current user", and the divergence between them is
exactly the class of bug this project exists to find.

**Translate everything to a Forms cookie internally.** Seriously proposed, because it is
the least code. Rejected because it makes the legacy credential the canonical one, which
means the migration can never finish — you would be minting the thing you are trying to
retire.

**Federate the legacy stacks into the IdP instead.** The right long-term answer and the
one this work is a step toward. Rejected as a first move because it requires the STS
owners to act, and they are in a business unit that has not yet agreed the project
exists. The coexistence bridge is what you build when you cannot make other people move.
