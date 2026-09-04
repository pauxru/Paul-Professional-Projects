# The argument

Six findings, in the order I would present them to someone deciding whether to fund the
work. Each one is a number from `docs/results.md` plus the thing the number changes.

---

## 1. Some migration work packages are impossible, and they look like they are behind schedule

**592 of 4096 authorization decisions diverge under a role-to-scope table. 72
counterexamples to monotonicity. With a deny channel: 16. With one corrected grant row:
0.**

The role-mapping work package is on every plan. It produces a table, the table is
reviewed, the review finds errors, the errors are fixed, and the count of known bugs goes
down. Everything about that process looks like progress.

It cannot converge, because a union of scopes is monotone and the source policy is not.
There is no amount of reviewing that fixes a shape mismatch.

The number that makes this actionable is the split. **576 of the 592 were structural.
16 were an ordinary data error** — a `Temp` role that had been given `ledger.read` it
never had. Both produce identical symptoms: a user can do something they should not.

The remedy always proposed for both is "go back and review the mapping more carefully".
That fixes the 16. Having fixed them, the team observes fewer bugs and concludes the
approach is working — and ships the 576.

**What changes:** before you build the mapping table, check whether the source policy is
monotone. It is a search over role-set pairs and it takes an afternoon. If it is not
monotone, the table is the wrong artefact and no schedule will save it.

## 2. The direction of a divergence matters more than the count

**All 592 are escalations. Zero are lockouts.**

I did not expect this. Prediction 3 said divergences would run both ways with lockouts
outnumbering escalations, on the reasoning that a hand-written table would forget grants
more often than it would invent them.

It is the opposite, and the mechanism is now obvious: the naive table forgets the
*negative* clauses, and forgetting a negative clause always grants. Every omission is an
escalation, structurally.

Which means the migration **passes UAT**. Users report doors that should be open and are
shut. Nobody reports the reverse — nobody has ever filed a ticket saying "I was allowed to
approve a payment I should not have been able to approve". A migration that produced 592
lockouts would be rolled back on day one and would therefore be the *safer* failure.

**What changes:** measure the direction, not just the count, and treat a clean UAT on an
authorization migration as absence of evidence. If your divergence set is all-escalation,
your acceptance testing is structurally incapable of finding it.

## 3. The password migration finishes about two years after the plan says

**50% by day 8. 90% by day 166. 95% by day 657. 99% never. Ceiling 96.0%.**

Rehash-on-login is genuinely elegant and the first fortnight is spectacular. Day 8 to 50%
is what makes it dangerous: the exponential that produces that headline is the same one
that produces the tail, and by the time the curve flattens the plan is approved.

The simulation and the closed form agree to 0.1 percentage points, so this is not a
modelling artefact — the arithmetic is right. The 4% ceiling is people who will not log in
again.

**What changes:** the cutoff date is a *budget decision*, not an observation. Picking the
date is picking a number of password resets, and that number is knowable in advance from
the curve. "We'll turn it off once usage drops" is choosing the same number without
looking at it.

## 4. The rollback window closes before you have any evidence

**Fewer than 5% rehashed: only until day 0. 17.9% after one day. 61% by day 14.**

This is the finding I found most uncomfortable, because it inverts something I believed.
The instinct on a risky migration is to ship carefully, watch it, and roll back if the
numbers look bad. That instinct assumes the rollback option persists while you gather
evidence.

It does not. Rolling back a hash migration after 61% of the fleet has been rehashed means
either abandoning those hashes or forcing a reset for the majority — which is the outage
you were trying to avoid.

And it gets worse the better things go: **the window closes fastest exactly where the
migration is healthiest**, because a healthy login rate is what rehashes people quickly.
The populations where you would most want a safety net are the ones that lose it first.

**What changes:** the go/no-go has to be made on pre-migration evidence, because the
evidence that arrives during the migration arrives after the decision has expired. In
practice that means the shadow-mode comparison — running both hash paths and diffing —
has to happen *before* you turn rehashing on, not concurrently with it.

## 5. A fully migrated account is only as strong as the weakest open door

**Forged legacy ticket against a fully migrated, Argon2id-hashed, OIDC-using account:
authenticates during coexistence, fails after the cutoff. 9,454 of 20,000 sessions still
valid 180 days after the announcement.**

The Argon2id hash is not part of the attack. It is not checked. Every dashboard showing
"% of users migrated to strong hashing" is measuring something real and reporting a
security improvement that does not exist yet.

The obvious fix — accept legacy tickets only for users who have not migrated — is worse
than nothing. A ticket is a bearer credential; the bridge learns who you are *from the
ticket*. An attacker who can forge one forges it for an unmigrated user, or for a name
with no directory row at all. The condition filters honest traffic and creates a live
oracle telling the attacker exactly who is still on the weak hash.

**What changes:** the door closes on a date, for everybody, enforced by rotating the
validation key — and the date cannot be reached by waiting, because a sliding window never
empties. Somebody has to switch it off while it is in use. → ADR 003

## 6. The mitigation that gets deferred is the one that pays for itself

**611x timing spread. 98.3% four-way format identification from one sample. With padding:
28.3% against a 25% baseline, and 66.7% binary against a 75% base rate — worse than
guessing.**

That channel enumerates precisely the accounts still on salted SHA-1, which are the
accounts whose hashes fall in hours if the database ever leaks. It exists *only* during
the migration, and it is widest at the start.

The cost of closing it:

| day | migrated | overhead |
| ---: | ---: | ---: |
| 1 | 17.9% | **5.55x** |
| 30 | 74.0% | 1.38x |
| 365 | 93.7% | **1.07x** |

**The overhead is time-varying, not a constant, and it converges to free.** I had
predicted a roughly constant ~2x and was wrong in a way that changes the recommendation.

The standard position is "defer the constant-time work until after the migration, when we
have capacity". That schedules the mitigation for the exact moment it stops being needed,
and skips it during the only window in which the oracle exists. Both properties — the cost
and the necessity — decay together, and they decay for the same reason.

**What changes:** budget 5.55x login CPU for week one. That is a real number and it is the
difference between a login tier that holds and one that queues. But it is a *week one*
number, and the honest framing to an operations team is "five times in week one, 1.4 times
in month one, noise by the end of the year" — not "it's fine", and not "we'll do it later".

---

## What I would tell a client on day one

Three things, before any code moves.

**Enumerate the authorization decision space and check monotonicity.** An afternoon. If
the policy is non-monotone, the mapping table on the plan is impossible and you have just
saved a work package that would otherwise consume a quarter and ship 576 escalations.

**Compute the migration curve from your own login telemetry.** You already have it. It
tells you the cutoff date, the number of password resets it implies, and — the part nobody
computes — the date by which rollback stops being available.

**Decide the cutoff date before starting, and write down what it costs.** Not "when usage
drops". A date, a number of resets, and a key rotation. The finding that makes this
non-negotiable is that a sliding-expiry credential population never empties: 9,454 of
20,000 still valid at 180 days. There is no observation that will ever tell you it is safe
to switch off. It is a decision, and deferring it is also a decision — one that keeps the
weakest door open indefinitely while the dashboard reports progress.
