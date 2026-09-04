# How it is built

Five assemblies, no packages, nothing mocked. The constraint that shaped everything is
that the findings had to be *measurements*, which means every component had to be real
enough to be attacked.

## Why write the crypto

`Auth.Passwords` implements Blake2b, Argon2d, Argon2i, Argon2id and PBKDF2 from RFC 7693,
RFC 9106 and RFC 8018. There is a perfectly good Argon2 package. I did not use it, for
two reasons.

The first is that the timing findings need control over the work. A finding like "a 611x
spread between formats gives you 98.3% format identification from one sample" is only
interesting if you know exactly what work each verification did. A library that
transparently picks a SIMD path based on CPU features is measuring the library.

The second is the more interesting one. Writing Argon2 from the RFC forced me to
encounter this:

```
m' = 4 * p * floor(m / (4 * p))
```

Memory is rounded down to a multiple of `4p`. But **H0 — the initial hash that seeds
everything — commits to the *requested* m, not the rounded one.** So at p=1, m=8 and m=11
fill byte-identical memory and produce **different tags**.

Think about what that means operationally. An engineer is told to increase the memory
cost. They change 8 to 11. It costs nothing extra, provides no extra security, and
invalidates every password hash in the fleet. The failure presents as every user's
password suddenly being wrong, with no error in any log, and no obvious relationship to
the one-character config change that caused it.

That is now a pinned test in `Argon2Tests.cs`. I would not have found it by reading the
package's README.

Two other things fell out of writing it:

- The variable-length hash `H'` has a loop that must terminate holding V_r, not V_{r+1}.
  Written naturally, it produces V_{r+1} and every output is wrong. The RFC vectors catch
  it instantly; nothing else does.
- H0 includes the **type byte**, so Argon2d, Argon2i and Argon2id differ at **block 0**.
  No two variants share any block at any point. I had assumed they diverged later, wrote a
  test asserting they agreed early, and the test was simply wrong about the algorithm.

47 vector and property checks run as their own stage in `test.ps1`, before anything else.
If Blake2b disagrees with RFC 7693, nothing downstream is evidence about anything.

## The legacy stack, reproduced faithfully including the bug

`Auth.Legacy` has two ticket protectors compiled from the same idea.

`LegacyTicketProtector` is the 2010 design: encrypt-then-MAC done in the wrong order, and
— crucially — **three distinguishable rejection reasons**. `BadPadding`, `BadMac`,
`Malformed`. It is a padding oracle, on purpose, because the comparison is the finding.

`HardenedTicketProtector` returns `BadMac` for everything. Including input that is not
valid hexadecimal.

That last part looks like a bug and is the entire property. Any rejection reason that
distinguishes "wrong key" from "wrong shape" is an oracle; the specific reason is real and
knowable inside the function, and telling anyone is what built the oracle in the first
place. There is a test asserting it, so anyone who "fixes" the apparent bug fails the
build and reads the comment.

Measured over four tampered tickets: legacy 3 distinct reasons, hardened 1.

## The bridge, which is the whole design

Everything upstream of `SessionBridge` is three incompatible authentication systems.
Everything downstream sees one type.

```csharp
public sealed record CanonicalPrincipal(
    string Subject,
    AuthenticationSource Source,
    IReadOnlySet<string> Scopes,
    IReadOnlySet<string> DenyScopes,
    string Tenant,
    DateTimeOffset AuthenticatedAt);
```

Two things about this are deliberate and load-bearing.

**`DenyScopes` exists because of the monotonicity problem.** A single scope set can only
express a monotone policy. The legacy policy is not monotone. So the principal carries a
negative channel, resolution is deny-wins, and each deny row is a mechanical transcription
of one `&& !IsInRole(...)` clause — which means a reviewer who does not understand the
business rule can still verify the translation by reading the two files side by side. That
takes divergence from 592 to 16. → ADR 002

**Which stacks are enabled is a `record`, not a deployment.**

```csharp
public sealed record StackConfiguration(
    bool AcceptLegacyFormsTickets, bool AcceptWsFederation,
    bool AcceptOidc, bool RehashOnLogin);
```

The rollback plan stops being a document and becomes a constructor argument. Every
intermediate state of the migration is something a test can construct, which means the
configuration you would reach for at 3am under pressure is the one the suite exercises
most.

It also makes the uncomfortable property visible in one place: because every enabled path
produces the *same* principal, an account's security is the security of the **weakest
enabled path**. That property is created by running three stacks, not by this class. What
this class does is put it somewhere you can see it and write a test against it, instead of
spreading it across three middleware pipelines where nobody can.

## The experiments are code, not prose

`Auth.Report` has one rule: every method answers exactly one question and returns **data**,
never a formatted string.

```csharp
public static DivergenceSummary Divergences(IClaimsTransformer transformer)
public static IReadOnlyList<(string Smaller, string Larger, string Resource)> Monotonicity()
public static TimingChannelResult TimingChannel(bool constantWork, int samples, HashPolicy policy)
public static (bool ReachableDuringCoexistence, bool ReachableAfterCutoff, string Subject) DowngradeExposure()
```

So the same call that produces a number for the document produces the number the test
asserts. There is no path by which the report can say 592 and the code produce something
else — `ReportTests` calls `Experiments.Divergences` directly and pins it.

`Monotonicity()` is worth a note. Rather than reasoning about whether the policy is
monotone, it searches: for every pair of role sets where one is a subset of the other, for
every resource, does the larger set lose an ability? One counterexample settles the
question. 72 says how pervasive it is. Searching is better than reasoning here because the
search does not have an opinion about what the policy was supposed to do.

## Two documents, because measurements have to be re-runnable

`ReportGenerator.Build(stable: bool)` emits two files.

`results.md` has the timings. `results-stable.md` has everything else, and stage 4 of
`test.ps1` regenerates it and byte-compares it against the committed bytes.

The obvious approach — regenerate the whole report in CI and diff it — cannot work,
because two of the findings are timing findings and those numbers change every run. The
tempting fixes are both bad: rounding hides the variance the reader needs, and comparing
with a tolerance requires a Markdown-aware numeric differ, which is a parser, which will
have its own bugs and will eventually be the reason someone turns the check off.

Predictions 5–8 are timing-dependent. In the stable file they are emitted with the verdict
`timing` and the evidence `see results.md`. They are not dropped. A scoreboard that
silently omits four rows is worse than one that admits it could not answer them — and the
count in the header is derived from the rows, so the two cannot disagree. → ADR 005

This check has paid for itself in ways I did not plan. Dictionary iteration order, a
culture-dependent number format, a `DateTime.Now` that crept into a header — all of them
surfaced as a failing byte comparison rather than as a subtly wrong document.

## The harness, and stage 5

Six stages: clean build → crypto vectors → full suite → report byte-identical → mutations
→ secrets.

Stage 5 is the one that decides whether the rest is worth anything. Six single-token
edits, each deleting exactly one check that a headline finding depends on:

| edit | what it removes | which sentence it invalidates |
| --- | --- | --- |
| `&& !In(Auditor)` | segregation of duties on the ledger | "72 counterexamples to monotonicity" |
| `&& !In(Temp)` | the Temp exclusion on user management | same family |
| deny row → `[]` | the auditor's ledger deny | "592 falls to 16" |
| `Has` drops the deny check | deny-wins resolution | the deny channel does anything at all |
| `MissingExpiry` → `None` | the missing-`exp` rejection | "17 of 17 attacks rejected" |
| `if (i < r - 1)` | the Argon2 H' off-by-one | every RFC 9106 vector |

All six killed. A survivor would mean the corresponding sentence in the README is an
unverified assertion, which is the only thing I actually want to know about a test suite.

The mutations attack *claims*, not lines. Mutating the arithmetic in Blake2b would prove
the tests notice broken arithmetic, which is not in doubt and not the claim.
