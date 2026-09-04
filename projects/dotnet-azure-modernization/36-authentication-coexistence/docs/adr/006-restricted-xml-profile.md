# 006. WS-Federation parses a deliberately restricted XML profile

**Status:** accepted
**Date:** 2026-03-11

## Context

The WS-Federation path consumes a signed SAML-ish assertion from an on-premises STS. To
validate it, something has to parse XML and check a signature over part of it. That
sentence describes the most reliably exploited component category in enterprise identity.

The attack class is **signature wrapping**: the signature is valid, over a real element,
and the code reads a *different* element. It works because XML Signature is defined over
a canonicalised node-set located by reference, and general-purpose implementations
resolve those references with enough flexibility to be steered. Variants:

- **Duplicate ID.** Two elements with the same `ID`; the signature verifier resolves one,
  the claims reader resolves the other.
- **Wrapped original.** The signed assertion is moved into a decoy wrapper element and an
  attacker-controlled assertion put in its place. The signature still verifies over the
  original, now unread.
- **Canonicalisation divergence.** Comment nodes, namespace declarations or whitespace
  that the canonicaliser removes but the reader honours.
- **DTD and entity expansion.** External entities, billion-laughs, and reading files off
  disk through a parser that was only meant to read a token.

The common cause is that the verifier and the reader are two different traversals of a
document rich enough for them to disagree. Every mitigation that keeps the rich document
is a patch on a structure that permits the bug.

## Decision

Do not parse general XML. Accept a **fixed shape** and reject everything else:

- one `Assertion` element, at a known path, with no siblings;
- a closed set of child elements in a fixed order;
- exactly one `Signature`, covering the entire assertion element — not a reference, not a
  fragment, not an `ID`-resolved node-set;
- DTD processing disabled, entity resolution disabled, no external resolvers;
- attribute values read from named attributes only, never by search;
- the signature verified over the **exact bytes received**, before any of them are
  interpreted as structure.

Anything not on that list is a rejection with a single failure reason, not a best-effort
parse.

The last point is the whole design. There is one traversal, not two: the bytes are
authenticated first, and only authenticated bytes are ever handed to the reader. Wrapping
requires a gap between "what was signed" and "what was read", and there is no gap to
occupy.

`Auth.Modern.WsFederation.Canonicalize` is correspondingly boring — a fixed
concatenation of the fields, in order, including `NotOnOrAfter`. It has no `Transforms`
element, because a transform is a program the attacker gets to write.

## Consequences

**All 17 token and assertion attacks in §5 of `docs/results.md` are rejected** — the
federation half includes duplicate-ID wrapping, wrapped-original, comment injection,
`NotOnOrAfter` tampering and unsigned-assertion substitution. Each is rejected because it
does not match the shape, which is a stronger reason than "the signature check caught it".

**This will not interoperate with a real identity provider**, and that is the cost. ADFS,
Okta and Azure AD emit assertions with `Transforms`, `KeyInfo` variants, optional
attribute statements and namespace prefixes chosen at run time. A production port must
either (a) constrain the STS to a fixed template and pin it, which is a negotiation with
whoever runs the STS, or (b) use a maintained library with a hardened configuration and
accept its history. The choice is between a compatibility problem and a vulnerability
class, and this project takes the compatibility problem deliberately because the goal was
to demonstrate that the vulnerability class is a *structural* consequence of a flexible
parser, not an implementation defect.

This is recorded in `docs/known-limitations.md`, because a reader who takes this code as a
drop-in WS-Federation implementation will have a bad afternoon.

**A restricted profile is also easier to review.** The parser is short enough to read in
one sitting, which is the property that actually produces security — a hardened
configuration of a large library is a claim about code nobody in the room has read.

## Alternatives considered

**`System.Security.Cryptography.Xml` with a hardened configuration.** The pragmatic
production answer. Rejected here because its safety depends on getting several
independent settings right, and because demonstrating "this configuration is safe" is
much harder than demonstrating "this shape is the only shape accepted".

**Drop WS-Federation and require the acquired business units to move to OIDC.** The right
end state. Rejected as a *precondition* for the same reason as in ADR 001: it depends on
a team that has not agreed the project exists. Coexistence engineering is what you do
when you cannot make other people move.

**Verify the signature, then re-serialise and re-parse to a canonical form.** Reduces the
gap but does not close it, because the re-serialiser is itself a traversal that can
disagree with the verifier. Rejected: fewer traversals is the fix, not more of them.
