# 004. Password verification pads to constant time during the migration

**Status:** accepted
**Date:** 2026-03-02

## Context

Rehash-on-login means the fleet holds four hash formats at once: `MembershipSha1` (salted
SHA-1, one iteration), `IdentityV2` (PBKDF2-HMAC-SHA1, 1,000 iterations), `IdentityV3`
(PBKDF2-HMAC-SHA256, 10,000 iterations) and `Argon2id` (RFC 9106, 64 MiB, t=3, p=4).

They do not cost the same. Measured medians on the development machine:

| format | median verify | ratio to PBKDF2-SHA1@1000 |
| --- | ---: | ---: |
| MembershipSha1 | 0.026 ms | 0.04x |
| IdentityV2 | 0.619 ms | 1.0x |
| IdentityV3 | 1.529 ms | 2.5x |
| Argon2id | 378.138 ms | 611x |

A 611x spread is not a side channel that needs statistics to detect. From **one** timing
sample per account, a four-way classifier identifies the stored format with **98.3%**
accuracy (chance is 25%).

That is an enumeration oracle for exactly the wrong population. An attacker who can time
login attempts can list every account still on salted SHA-1 — the accounts whose hashes
fall in hours if the database ever leaks — and concentrate on them. The oracle exists
*only during the migration*, and it is strongest at the start, when the unmigrated
population is largest and most valuable.

## Decision

During coexistence, password verification pads to a fixed floor: the work is done, and
then the operation sleeps until a constant deadline that is at least the cost of the
slowest enabled format.

With padding, four-way identification falls from 98.3% to **28.3%** against a chance
baseline of 25%. The binary question — "is this account unmigrated?" — falls from 100% to
66.7% against a **base rate of 75%**, meaning the padded classifier does worse than
always answering "yes". There is no residual signal.

## Consequences

**The cost is real and it is temporary.** Padding forces every verification to pay the
Argon2id price, including the 95% of logins that are already on the cheap path early in
the migration. But "every login costs the slowest format" is only expensive while the
formats are unequal, and the whole point of the migration is to make them equal.

Modelled against the typical population's migration curve, the padding overhead is:

| day | fraction migrated | padding overhead |
| ---: | ---: | ---: |
| 1 | 17.9% | **5.55x** |
| 30 | 74.0% | 1.38x |
| 365 | 93.7% | **1.07x** |

**The overhead is time-varying, not a constant, and it converges to free.** The
mitigation retires itself.

This inverts the argument that is always made against padding. The usual position is
"defer the constant-time work until after the migration, when we have capacity" — which
schedules the mitigation for precisely the moment it stops being needed, and skips it
during the only window in which the oracle exists. The correct reading is the opposite:
padding is most expensive exactly when it is most necessary, and both properties decay
together.

**Operational cost is a capacity question, not a latency question.** 5.55x on day 1 is a
real number and it must be planned for: it is the difference between a login tier that
holds and one that queues. The honest framing to give an operations team is "week one
costs five times the login CPU, month one costs 1.4 times, and by the end of the year it
is noise" — not "it's fine".

## Alternatives considered

**Do nothing; accept the oracle for the duration.** The default, and defensible if the
migration is short. §2 of the report is why it is not defensible here: the typical
population takes 166 days to reach 90% and 657 to reach 95%. "For the duration" means
about two years.

**Rehash everything offline by wrapping the old hashes** (`Argon2id(PBKDF2(password))`).
Eliminates the mixed-format window entirely and is the right answer when it is available.
Rejected here because the legacy `MembershipSha1` verification path is implemented in a
stored procedure that other systems still call, and changing the stored format breaks
them. Recorded as the recommended next step once those callers are gone — it is a better
answer than padding, and it was unavailable rather than wrong.

**Pad only the fast formats up to `IdentityV3`, not to Argon2id.** Cheaper, and it
collapses three of the four formats. Rejected because it leaves Argon2id distinguishable,
which inverts the oracle: instead of finding unmigrated accounts, the attacker finds
migrated ones and infers the rest by subtraction. A partial mitigation of an
identification channel is usually not a partial mitigation.

**Add random jitter instead of padding to a floor.** Rejected: jitter is averaged out by
repeated sampling, and an attacker enumerating accounts is sampling repeatedly by
construction. Padding to a deadline removes the signal; jitter only raises the number of
samples needed.
