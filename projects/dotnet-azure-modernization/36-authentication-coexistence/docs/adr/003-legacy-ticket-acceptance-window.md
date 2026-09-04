# 003. The legacy ticket acceptance window is a date, not a condition

**Status:** accepted
**Date:** 2026-02-21

## Context

The obvious way to retire the Forms authentication path is to make it conditional:
accept a legacy ticket **only if** the user has not yet been migrated. It reads well in a
design document. Every migrated user is immediately protected; the legacy door closes
per-user, automatically, as the migration proceeds; no date has to be negotiated with
anybody.

It does not work, and the reason is the subject of §4 of `docs/results.md`.

A Forms ticket is a bearer credential. The bridge learns the user's identity **from the
ticket**, and the ticket is presented by whoever holds it. To apply the rule "accept only
if this user is unmigrated", the bridge must first decide who the user is — which means
trusting the ticket — and then look them up. An attacker who can forge a ticket forges it
for whoever they like; if they forge one for a migrated user, the condition rejects it,
so they forge one for an unmigrated user instead, or simply for a name that has no
directory row at all.

The condition therefore filters *honest* traffic and not attack traffic. Worse, it
creates a live oracle: the difference between "rejected because migrated" and "rejected
for another reason" tells the attacker who is still on the old hash — the exact
population whose passwords are cheapest to crack.

Measured: with a fully migrated account holding an Argon2id hash, a forged legacy ticket
authenticates successfully during coexistence (`ReachableDuringCoexistence == true`) and
fails after the cutoff (`ReachableAfterCutoff == false`). The Argon2id hash is not part
of the attack at any point. **Improving a user's credential does not improve their
account until the other doors are shut.**

## Decision

Legacy Forms tickets are accepted based on a **date**, held in `StackConfiguration`, and
on nothing else. There is no per-user condition, no "unmigrated only" rule, no
grandfathering by cohort.

The date is announced, the door closes for everybody at once, and the population that has
not migrated by then is handled by a password reset — a support cost, budgeted, rather
than a security property nobody can state.

## Consequences

**The cutoff cannot be reached by waiting.** §3 of the report is the argument: sliding
30-day expiry means the legacy population decays but is continuously refreshed, and
9,454 of 20,000 sessions are still valid 180 days after the announcement. There is no
date at which the legacy path becomes unused. Somebody has to switch it off while it is
still in use, which is a decision, not an observation — and it is the decision teams
avoid making by telling themselves they will do it "once usage drops".

**The support cost is knowable in advance.** §2 gives the migration curve for a typical
population: 50% by day 8, 90% by day 166, 95% by day 657, and a ceiling of 96.0% — the
remaining 4% never log in again. Choosing the cutoff is therefore choosing a number of
password resets, and that is a number a business can weigh. Announcing the date and
"seeing how it goes" is choosing the same number without looking at it.

**Rollback is not available for long.** §3: the window during which fewer than 5% of
users have been rehashed lasts until **day 0**, and 61% are rehashed by day 14. Rolling
back after that means either abandoning the new hashes or forcing a reset for the
majority. The window closes fastest precisely where the migration is healthiest — the
better the login rate, the sooner rollback stops being an option. So the go/no-go
decision has to be made on *pre-migration* evidence, because the evidence that arrives
during the migration arrives after the decision has expired.

## Alternatives considered

**Per-user conditional acceptance.** The subject of this ADR. Rejected: it is not a
security control, and it leaks the migration state of every account.

**Shorten the sliding window first, then cut over.** Genuinely helps and is
complementary — dropping 30 days to 7 collapses the tail in §3. Rejected as a
*substitute* because it still never reaches zero, and because shortening the window is
itself a user-visible change that needs its own announcement. Worth doing before the
cutoff, not instead of it.

**Rotate the Forms validation key at cutoff.** Adopted as the enforcement mechanism, not
as an alternative. Turning off the code path is the decision; rotating the key is what
makes it true even for a deployment that missed the config change.
