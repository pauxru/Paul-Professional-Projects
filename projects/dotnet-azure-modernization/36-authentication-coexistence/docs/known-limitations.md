# Known limitations

Everything here is a place where this code would be wrong, misleading or unsafe if taken
at face value. It is written for the reader who is deciding how much of `docs/results.md`
to believe, and for the one who is thinking about lifting a file out of it.

## The measurements

**The authorization decision space is the one I modelled.** 4096 decisions is the
exhaustive product of six roles, 2^6 role subsets, eight resources and a request context
with three boolean flags. The finding that the legacy policy is non-monotone — and that a
role-to-scope union therefore *cannot* reproduce it — is proved for this policy. It is not
proved for every legacy policy. What transfers is the method: enumerate the decision
space, diff the two implementations, and check monotonicity before assuming a mapping
table can exist. The real system this is modelled on has 31 roles, which is 2^31 subsets;
exhaustive enumeration is not available there and the equivalent check would have to be
property-based or symbolic.

**Argon2id here is a managed implementation and is roughly 4–5x slower than libargon2**
at the same parameters, because there is no SIMD. The consequences are asymmetric:

- *Ratios between algorithms are meaningful.* The 611x spread over PBKDF2-SHA1@1000 is
  real, and it is the number the timing-channel argument in ADR 004 rests on.
- *Absolute milliseconds are not.* 378 ms for Argon2id at 64 MiB / t=3 / p=4 is not a
  number to size a login tier with. A production deployment should re-measure and
  re-tune the parameters to a target latency, and 64 MiB / t=3 / p=4 is an RFC 9106
  recommendation, not a result of this work.
- *The timing channel is easier to observe here than in production.* A slower Argon2id
  makes the four-way classifier's job easier. The 98.3% identification rate is an upper
  bound.

**The timing channel was measured on a loaded developer machine, over a local function
call.** There is no network, no TLS, no load balancer and no other tenant. A remote
attacker sees strictly less signal than this — so the leak figure is an upper bound and
the padding cost is a lower bound on what it takes to close it. The direction of both
errors is stated because it is the only thing that makes a measurement on a laptop
usable.

**The migration model is a model.** Login arrivals are Poisson, one rate per cohort
(`DailyLoginProbability = 1 - exp(-1/mean)`). Real populations have weekly seasonality,
leavers, service accounts, shared logins and a support queue that resets passwords out of
band. The simulation agrees with the closed form to within 0.1 percentage points (93.7%
vs 93.6% at one year), which demonstrates that the arithmetic is right — not that the
assumption is. The shape of the finding (a long tail, a ceiling below 100%, a rollback
window that closes early) is robust to the parameters; the specific days are not.

**Nothing here was measured under concurrency.** No lock contention, no connection pool,
no rehash storm when a popular service account logs in. Rehash-on-login has a
well-known operational failure mode — a thundering herd at the start of the business day,
each request paying Argon2id — and this project does not model it.

## The implementation

**The WS-Federation profile is deliberately restricted and will not interoperate with a
real identity provider.** See ADR 006. It accepts one fixed assertion shape and rejects
everything else, which is what makes signature wrapping structurally impossible and also
what makes it useless against ADFS, Okta or Entra ID. Do not drop `WsFederation.cs` into
a system that has to talk to an STS you do not control.

**The Forms ticket implementation reproduces a vulnerability on purpose.**
`LegacyTicketProtector` reports three distinguishable rejection reasons (`BadPadding`,
`BadMac`, `Malformed`) and is a padding oracle. That is the point of the comparison with
`HardenedTicketProtector`, which reports one reason for everything. **Do not use
`LegacyTicketProtector` for anything.** It is a museum piece with a working mechanism.

**`HardenedTicketProtector` returns `BadMac` for input that is not even hex.** This looks
like a bug and is the central property: any rejection reason that distinguishes "wrong
key" from "wrong shape" is an oracle. A test asserts it, so anyone "fixing" it will find
out.

**The bridge does not check the directory on the legacy paths.** A Forms ticket or a
WS-Federation assertion for a subject that does not exist authenticates successfully; the
OIDC path refuses. This is faithful to the system being modernised — the old application
took its roles from the cookie — and it means **deprovisioning does not take effect on the
legacy paths**. The principal gets no tenant, so tenant-scoped checks fail closed, which
narrows but does not close the hole. Adding the check is one line and is a behaviour
change that will lock out anyone whose directory row is missing for an unrelated reason,
so it is recorded here rather than made silently. `SessionBridgeTests` pins the current
behaviour so the decision cannot be reversed by accident.

**No clock skew, no key rotation, no distributed anything.** Every stack runs in-process
against a fixed `DateTimeOffset`. Real coexistence deployments fail in ways this cannot
see: key rotation races between nodes, sticky sessions pinning a user to a node with the
old configuration, and the two minutes of skew that make a short-lived token
intermittently invalid for one region.

**Key management is out of scope.** Keys are generated with
`RandomNumberGenerator.GetBytes` at startup and held in memory. A real deployment needs
Key Vault or equivalent, rotation with an overlap window, and a story for what happens to
in-flight tickets during rotation — which is most of the operational difficulty of the
cutoff in ADR 003.

## The harness

**Stage 4 verifies `results-stable.md`, which is the less interesting of the two report
files.** Timing-derived findings are excluded from it by construction (ADR 005), so if the
timing measurement broke entirely, stage 4 would still pass. The mechanism is covered by
`Auth.Tests`; the numbers are only covered by rerunning the report and reading it.

**Six mutations is a sample, not a coverage metric.** They were chosen to attack the
specific checks the report's headline findings depend on. 6/6 killed means those six
sentences are verified. It does not mean the suite would catch an arbitrary defect.

**The mutation stage excludes `ReportTests`** because report generation takes minutes and
none of the mutations targets report formatting. If a mutation ever needed `ReportTests`
to be caught, that would be a signal that the behavioural tests are too weak — not that
the filter is wrong.
