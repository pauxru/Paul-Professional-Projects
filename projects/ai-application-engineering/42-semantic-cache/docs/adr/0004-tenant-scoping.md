# ADR 0004 — Tenant scoping is a correctness boundary, not a tuning parameter

**Status:** accepted

## Context

A shared semantic cache in a multi-tenant product is attractive: `acme` warms an
entry, `globex` gets a free hit, hit rate goes up. Many questions genuinely are
shared — "how do I reset my password" means the same thing to everyone.

The corpus contains three intents whose wording is **byte-identical** across
`acme`, `globex` and `initech`, with three different correct answers. Questions
like "what does my plan include" are answered by the tenant's own configuration.

These are not near-misses. The inputs are the same bytes. Cosine similarity is
1.000. **No encoder can separate them**, no threshold can reject them, and the
top-K veto passes them trivially because the decisive sets are identical. Every
mechanism in this repository is powerless, by construction.

## Decision

Partition the cache by tenant. A lookup considers only entries belonging to the
asking tenant. `ScopeByTenant` is on by default.

The reasoning is that the mechanism must match the problem's structure. The
other defences in this project are *statistical* — they reduce the probability
of confusing two things that are merely similar. This failure is not
probabilistic. Two requests are identical and must receive different answers, so
the only thing that can distinguish them is information outside the text. That
information is the tenant, and the only way to use it is to make it part of the
key rather than part of the score.

Put differently: **you cannot fix a data-boundary problem with a similarity
function**, no matter how good the similarity function is. Trying to is how
cross-tenant leaks ship.

## Measured cost and benefit

| configuration | hit rate | entries | cross-tenant leaks |
|---------------|----------|---------|--------------------|
| shared cache | 93.4% | 137 | **96** |
| scoped by tenant | 90.6% | 241 | **0** |

Scoping costs 2.8 points of hit rate and 104 extra entries, and eliminates 96
answers that were confidently, silently wrong — served at similarity 1.000, the
score a system is *most* confident about.

That is the shape of the trade and it is not close. The 2.8 points are real
money; the 96 leaks are a customer seeing another customer's billing terms.

## Consequences

- **Genuinely shared answers are cached per tenant**, costing memory and cold
  misses. The corpus marks shared intents, and a production system could keep a
  shared partition for a curated allow-list of intents. That is a deliberate,
  reviewable exception rather than a default. It is not implemented here because
  the interesting question — how you decide what is safe to share — is a policy
  question, not a caching one.
- **Scoping is invisible within a single tenant.**
  `TestScopingDoesNotAffectSingleTenantTraffic` asserts that every decision for
  `acme` is identical with and without it. The cost is entirely the loss of
  cross-tenant reuse, which is what it should be.
- **The guarantee is structural, so it is tested structurally.**
  `TestScopedAnswersAlwaysBelongToTheAskingTenant` asserts that every answer
  served corresponds to an intent that tenant actually has — not that leaks are
  rare, but that they are impossible. `TestUnscopedCacheLeaksAcrossTenants`
  asserts the trap is live, so the first test cannot pass vacuously.
- The same argument applies to any other axis where identical text must produce
  different answers: locale, environment, API version, entitlement tier. Each is
  a partition key, not a feature.
- Coalescing needs the same boundary for the same reason, and the gateway's
  `joinable` check compares tenants before considering similarity.
