# 002. Authorization carries a deny channel, not just a scope set

**Status:** accepted
**Date:** 2026-02-14
**Supersedes:** the role-to-scope mapping table drafted in the migration plan

## Context

The migration plan contained a table. Six legacy roles down the left, a set of modern
scopes across the top, ticks in the cells. It had been reviewed twice. It is the standard
artefact for this kind of work and it was wrong in a way that no amount of further review
would have found.

The legacy authorization code is a `switch` written over about fifteen years. Most of it
is what you would expect. The interesting parts are the negative clauses:

```csharp
Resources.WriteLedger =>
    (In(Admin) || In(Clerk)) && !In(Auditor),

Resources.ManageUsers =>
    In(Admin) && !In(Vendor) && !In(Temp),
```

These are segregation-of-duties rules. Somebody needed to carve an exception, and the
cheapest place to put it was a `!`.

A role-to-scope table computes a **union**: your scopes are the union of the scopes of
your roles. Union is *monotone* — adding a role can only add scopes. The legacy policy is
not monotone: `Admin` can write the ledger, and `Admin + Auditor` cannot.

A monotone function cannot agree everywhere with a non-monotone one. This is not a
statement about how carefully the table was reviewed. It is a statement about the shape of
the two functions, and it means **no table of that shape can ever be correct** — not this
table, not the next one, not the one produced after a third review.

`docs/results.md` §1 enumerates all 4096 decisions (6 roles × 2^6 role subsets × 8
resources × request context) and finds:

| transformation | divergences | escalations | lockouts |
| --- | ---: | ---: | ---: |
| naive role-to-scope table | 592 | 592 | 0 |
| with a deny channel | 16 | 16 | 0 |
| deny channel, one grant row corrected | 0 | 0 | 0 |

and 72 distinct pairs of role sets where adding a role removes an ability.

## Decision

`CanonicalPrincipal` carries **two** sets: `Scopes` and `DenyScopes`. Resolution is
deny-wins:

```csharp
public bool Has(string scope) => !DenyScopes.Contains(scope) && Scopes.Contains(scope);
```

Each deny row is a direct transcription of one `&& !In(...)` clause. The transcription is
mechanical, which matters: it can be checked by reading the two files side by side, and
a reviewer who does not understand the business rule can still verify the translation.

## Consequences

The 592 divergences fall to 16. The remaining 16 came from a single wrong grant row —
`Temp` had been given `ledger.read`, which it never had — and correcting it takes the
count to 0.

That split is the most useful number in the project. **576 of the 592 were structural**
and unreachable by any grant table however carefully reviewed; **16 were an ordinary data
error** of the kind a review does catch. They produce *identical symptoms*. The remedy
invariably proposed for both — "go back and check the mapping more carefully" — fixes
only the 16, and having fixed them, the team observes fewer bugs and concludes the
approach is working.

**Every one of the 592 is an escalation. None is a lockout.** So the failure mode is
silent: nobody files a ticket saying "I was allowed to do something I should not have
been." A migration whose failures are all escalations passes UAT. It passes UAT
*because* it is broken in the direction that does not generate complaints.

**Cost.** Deny scopes are a second thing to get right, and a deny row that should not
exist locks people out — the loud failure mode, which is why it is the safer place to put
the risk. Downstream code must go through `Has(...)`; a direct `Scopes.Contains(...)`
bypasses the deny channel entirely. That is why `Has` is the only public read path and
why one of the mutations in `test.ps1` deletes the deny clause from it.

## Alternatives considered

**A bigger table with composite keys** (`Admin+Auditor` as its own row). Works, and grows
as 2^n. At six roles that is 64 rows; the real system has 31 roles. Rejected.

**Port the `switch` verbatim into the new system.** Rejected: it keeps the policy in code,
which is what made it invisible for fifteen years. The deny channel is data, and data can
be diffed, reviewed and exported to the compliance team.

**A general policy engine (OPA, Cedar).** The right answer for a green field. Rejected
here because it converts a bounded migration into an unbounded one, and because the value
of this exercise was *discovering* that the policy is non-monotone — which you find by
enumerating the decision space, whatever you plan to run afterwards.
