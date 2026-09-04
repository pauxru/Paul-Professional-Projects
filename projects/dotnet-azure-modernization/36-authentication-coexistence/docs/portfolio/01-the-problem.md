# The problem

## The sentence in the plan

Every authentication modernisation plan I have read contains this sentence, or something
close enough that you could swap them without anyone noticing:

> Users will be migrated to the new identity provider on a rolling basis, with full
> cutover by the end of Q3.

It is a good sentence. It is specific, it has a date, it acknowledges that the migration
is gradual. It gets approved.

It also contains three claims that are not schedule risks. They are arithmetic, and they
are false, and nobody checks them because they do not look like the kind of thing you
check. They look like project management.

## Claim one: we'll map the old roles to the new scopes

This is the first work package on every one of these plans, and it produces a table. Six
legacy roles down the side, a set of modern scopes across the top, ticks in the cells.
Somebody spends a week on it, somebody else reviews it, and it goes into the design
document as a settled question.

Here is the legacy authorization code it is meant to replace. This is not a caricature; it
is the shape that fifteen years of exceptions produces:

```csharp
case "ledger.write":
    return (IsInRole("Admin") || IsInRole("Clerk")) && !IsInRole("Auditor");

case "users.manage":
    return IsInRole("Admin") && !IsInRole("Vendor") && !IsInRole("Temp");
```

Those `!` operators are segregation of duties. Somebody in about 2013 was told that an
auditor must not be able to write the thing they audit, and the cheapest place to express
that was a negation in a `switch`.

Now consider what a role-to-scope table computes. Your scopes are the union of the scopes
granted by each of your roles. Union has a property: adding a role can only ever *add*
scopes. The function is **monotone** in the role set.

The legacy policy is not monotone. `Admin` can write the ledger. `Admin + Auditor` cannot.
Adding a role took an ability away.

And a monotone function cannot agree everywhere with a non-monotone one. Not "is unlikely
to". Cannot. This is a fact about the shape of the two functions, and it means the table
is not merely wrong — **no table of that shape can ever be right**. Not this one, not the
one produced after the review, not the one produced after the third review. The work
package is not behind schedule. It is impossible, and it will consume however much time
you give it.

I enumerated the whole decision space to see how bad it gets: six roles, all 64 role
subsets, eight resources, a request context with three flags. 4096 decisions.

**592 diverge.** And **72 distinct pairs** of role sets exist where adding a role removes
an ability — so the non-monotonicity is not one weird rule in a corner. It is pervasive.

## Claim two: rehash on login, it'll be done in a quarter

The password migration story is the one everybody is comfortable with, because it is
genuinely elegant. You cannot rehash a password you do not have. But you have it,
briefly, every time the user logs in. So: verify against the old hash, and if it succeeds,
immediately re-store it with the new algorithm. No password reset email, no support
tickets, no user-visible change at all.

It works. The question is when it *finishes*, and the answer has the shape of every
arrival process:

| milestone | day |
| --- | ---: |
| 50% | 8 |
| 90% | 166 |
| 95% | **657** |
| 99% | never |

The ceiling is **96.0%**. The remaining 4% are not slow. They are gone — accounts that
will not log in again, and no amount of waiting converts them.

Day 8 to 50% is what makes this hard to see. The first two weeks look spectacular. Every
dashboard says the migration is working. The exponential decay that produces "50% in 8
days" is the same one that produces "657 days to 95%", and by the time the curve
flattens, the plan has been approved on the strength of the first fortnight.

## Claim three: once everyone's migrated we can turn the old path off

The third claim has a hidden dependency on the second, and it fails twice.

First, "everyone" never happens — see above. The plan quietly becomes "when usage drops
low enough", which is a threshold nobody wrote down and therefore nobody can be
accountable for crossing.

Second, and worse: the old path being *open* is the problem, independently of whether
anyone uses it. The Forms authentication ticket is a bearer credential. It says who you
are, it is encrypted and MACed with a key, and anyone who can produce one is that person.

So take a user who has done everything right. Migrated to OIDC. Fresh Argon2id hash at
64 MiB. Has not touched the legacy application in months.

Forge a Forms ticket for them.

It authenticates. During coexistence: **true**. After the cutoff: **false**. The Argon2id
hash is not involved at any point in the attack — it is not checked, not consulted, not
relevant. Improving a user's credential does not improve their account while another door
is open.

And you cannot wait the door out. The Forms cookie has a 30-day *sliding* expiry, so the
population holding a valid legacy credential is continuously refreshed. Simulated over
20,000 users: **9,454 sessions still valid 180 days after the cutoff was announced.**
There is no date at which the legacy path becomes unused. Somebody has to switch it off
while it is still in use, which is a decision — and it is exactly the decision teams avoid
making by telling themselves they will do it once usage drops.

## The thing underneath all three

Each of these claims fails in the direction nobody is looking.

The mapping table fails toward **escalation**: all 592 divergences give users more access
than the legacy system did, and zero give them less. So the migration passes UAT. It
passes UAT *because* it is broken — nobody files a ticket saying "I was allowed to do
something I shouldn't have been". A migration that produced 592 lockouts would be rolled
back on day one, which is why it would be the safer failure.

The password migration fails toward **looking finished**. The first fortnight is
brilliant. The tail is where the risk lives and the tail is invisible from a dashboard
that shows a percentage going up.

The cutover fails toward **appearing safe**. Every user who moves to OIDC feels like
progress, and every one of them is still reachable through the old door.

That is the actual subject of this project. Not "authentication is hard" — everyone knows
that. The specific claim is: **a coexistence migration produces failures that are
systematically invisible to the checks people actually run**, and the only way to see them
is to enumerate the decision space, simulate the population, and forge the credential
yourself.

So I did. `docs/results.md` is what came back. I wrote fourteen predictions first, before
running anything, because a measurement you can rationalise after the fact is not a
measurement.

Eleven of them were wrong.
