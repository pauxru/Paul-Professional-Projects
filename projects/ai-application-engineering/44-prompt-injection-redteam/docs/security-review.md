# Security review

A review of the defences implemented here, written as though they were going
into production. The subject is the *design*; the measured numbers are in
`docs/results.md`.

---

## Threat model

**Adversary.** Controls the full byte content of anything the agent retrieves:
emails, web pages, documents, tool output. Knows the system prompt, the
defence stack, the normaliser's source, and the classifier's weight table.
Cannot modify code, cannot observe the nonce used for delimiters, cannot
compromise the model provider.

**Assets.** (1) Tool invocation — anything with a side effect. (2) Secrets in
context: system prompt, credentials, prior conversation. (3) The integrity of
what the agent tells the user.

**Trust boundary.** Between the principal's turn and everything the agent
retrieves. This is the boundary `channels.py` implements, and it is drawn per
character rather than per message, because a single prompt contains text from
both sides of it.

---

## Control-by-control assessment

### Capability broker — the only one that carries a guarantee

Four rules, evaluated in order: unknown tool, requester trust, argument taint,
unattributable argument.

**Strength.** The guarantee is stated as a property, not as a rate: *no tool
call executes unless the principal is authorised for that tool and every
sensitive argument is shown to originate at or above the tool's trust bar*.
It holds independent of the model. Section 3 demonstrates it by sweeping model
compliance end to end and observing a flat line.

**Residual.** The guarantee's boundary is exact and is the thing to understand
before deploying it: **a broker reduces the tool-call attack surface to the set
of actions the principal asked for themselves.** If the user asks the agent to
send an email and an injection changes only the recipient, the argument rules
must catch it, and they can only do so when the argument's provenance is
determinable.

**The dangerous case is paraphrase.** When the model restates an injected
argument in its own words, the taint tracker has no evidence in either
direction. Section 9 measures this: 30% of argument-stage denials come from the
unattributable rule. A broker that treated "cannot determine" as "trusted" —
the natural way to write it, since the check reads as a lookup that returned
nothing — would allow exactly those calls and produce an audit log in which
every entry says *allowed* and every entry is true.

**Two findings, both fixed, both in this class.** The requester was originally
taken from the payload's arrival channel rather than the turn's principal,
which made the broker look far stronger than it is (bug 4). And the argument
loop skipped empty values, so `send_email(to="")` was allowed having been
checked by nothing (bug 7).

### Egress filter — a guarantee about bytes, scoped to modelled channels

**Strength.** Separates zero-click channels (auto-fetched images, prefetch)
from click-required ones, and blocks the former to unknown hosts even in
permissive mode. That is the right default: a rendered image is exfiltration
with no user action, and a link is not.

Host matching is dot-anchored, so `evil-example.com` does not match an
allowlist entry for `example.com`. Userinfo spoofing
(`https://example.com@attacker.net/`) is handled — the host is the part after
the `@`, and a filter that splits on `.` or takes a prefix gets this wrong.

**Residual.** Only two channels are modelled. DNS, timing, and content the
user will paste elsewhere are not. Strict mode costs false positives (measured
in section 7) and every deployment has to make that trade explicitly.

### Normaliser — deterministic, and deliberately incomplete

**Strength.** Tag blocks, zero-width characters, bidi controls, mixed-script
confusables, one level of base64/hex/URL decoding, C0 stripping. Deterministic
and idempotent, both asserted.

**Residual.** Partial confusable table, no rot13, no nested decoding
(ADR-0004). Attacks defeating each are in the corpus.

**Finding, fixed.** C0 control characters were *flagged and not removed* (bug
8). Two `\x01` bytes took a payload from a classifier score of 0.80 to 0.00
while the normaliser truthfully reported finding two control characters.
Detecting an evasion and neutralising it are different actions, and a flag
without a consequence makes the failure harder to see rather than easier.

**Finding, fixed.** Unscoped confusable folding mangled benign non-Latin text
(bug 1) — an availability failure correlated with the user's language.

### Classifier — a probability reducer, correctly labelled

**Strength.** Cheap, and its combination with normalisation is where the
defence-in-depth actually lives: 1.7% solo, 4.7% in interaction.

**Residual.** Hand-chosen weights (ADR-0005). An adversary who reads
`defenses.py` can construct a payload below threshold; the corpus contains
several. Section 6 finds no threshold reaching 90% detection at any false
positive rate — the classifier is a filter, not a control, and must never be
the last line.

### Spotlighting — behavioural, and parameter-sensitive

**Strength.** Cheap, no false positives, and it composes with everything.

**Residual.** Its entire contribution is a multiplier chosen by the author of
the simulation. Its Shapley rank is explicitly *not* a result.

**Finding, fixed.** It was applied to the principal's own turn (bug 9) —
telling the model that the user's message is data to be ignored. In production
that breaks the assistant; in measurement it inflated the layer's value.

---

## Residual risk after the full stack

Nine of 81 attacks survive (11.1% [6.0%, 19.8%]). Eight pursue misinformation
or override goals that produce **no tool call and no egress**, so no structural
layer applies by construction. The ninth is a tool call the user themselves
requested.

Section 10 states the consequence plainly: an agent that can write text to a
user can mislead that user, and no filter on actions or on bytes leaving the
system addresses that. The controls in this repository are about capability
and about exfiltration. They are not about truthfulness.

## Recommendations for a production adaptation

1. **Deploy the broker first.** It is the only layer whose value does not
   depend on which model you are running.
2. **Write the unattributable rule as a positive requirement.** An argument
   must be shown to be trusted, not merely not shown to be untrusted.
3. **Track provenance per character.** Message-level tracking collapses on the
   first prompt that mixes a system instruction with a retrieved document.
4. **Never let the classifier be the last line.** Section 6.
5. **Use nonce delimiters.** An attacker cannot close a fence whose tag was
   chosen after their payload was written. Section 8 measures the difference:
   25.0% against 8.3%.
6. **Log allow decisions, not just denials, and include the reason.** The
   failure mode of a taint system is a clean audit log.
