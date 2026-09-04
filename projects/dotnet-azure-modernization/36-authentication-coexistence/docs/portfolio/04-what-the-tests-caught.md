# What the tests caught

Four things the suite found in my own code, and what each one says about where to point a
test.

---

## 1. A missing `exp` claim produced a permanent token

This is the one I would show someone who wanted to know why the tests exist.

```csharp
if (claims["exp"] is { } exp && seconds >= exp.GetValue<long>())
{
    return new JwtValidation(null, JwtFailure.Expired);
}
```

Read it. It looks careful. It is a pattern-matching null guard — the modern C# idiom for
"handle the case where this might not be there" — followed by the expiry comparison. It is
the kind of line that gets a tick in code review because it demonstrates the author
thought about absence.

What it does is skip the expiry check entirely when `exp` is missing. Omit the claim, and
the token never expires. Forever. On a signed token, so everything else about it is
perfectly valid.

The bug is **shaped like defensive code**, which is why it survived being written, read
back, and reviewed by me twice. The null guard idiom encodes "if it's there, check it",
and for optional data that is right. For a security claim, absence has to be a rejection,
not a skipped check — and there is nothing in the syntax that distinguishes the two cases.

The test that found it was not clever:

```csharp
[Fact] public void ATokenWithNoExpiryIsRejected()
```

It exists because I was writing tests for every `JwtFailure` value and noticed there was
no failure mode for "no expiry". Enumerating the enum found the hole. The fix added
`JwtFailure.MissingExpiry`, and a mutation in `test.ps1` reverts it so the suite has to
keep catching it.

**The generalisation:** in security code, be suspicious of any construct that makes
"absent" and "present but fine" take the same branch. `is { }`, `?.`, `??`,
`TryGetValue` with a default, `GetValueOrDefault` — all of them turn a missing value into
a silently permissive one, and all of them read as diligence.

## 2. The prediction scoreboard was quietly emitting four of fourteen rows

The report ends with a table scoring every prediction against what actually happened. It
was printing four rows.

The loop scoring the predictions was bounded by the wrong collection, so predictions 5–14
were never evaluated. And the failure was *invisible* — a scoreboard with four rows looks
exactly like a project that made four predictions. Nothing was red. No exception, no
warning, no empty cell. The document was internally coherent and wrong.

This is the failure mode that worries me most in generated reports: not a wrong number,
but a **missing section that leaves no hole**. A wrong number gets argued with. A missing
row gets read as "that wasn't measured".

Two fixes, and the second matters more:

- Score all fourteen. Timing-dependent predictions (5–8) cannot be scored in the
  byte-stable file, so they are emitted with the verdict `timing` and the evidence "see
  results.md". Not dropped — a scoreboard that silently omits rows is worse than one that
  admits what it could not answer here.
- **Derive the header count from the table.** The header used to say "14 predictions"
  because 14 was written in the source. Now it counts the rows it is about to print, and a
  test asserts the two agree:

```csharp
var rows = report.Split('\n').Count(l => l.Contains("| contradicted |") || ...);
Assert.Contains($"**{rows} were contradicted.**", report);
```

`test.ps1` stage 4 does the same with a regex backreference, so no threshold is
maintained by hand.

**The generalisation:** a generated document should not be able to disagree with itself.
Any number that appears in prose *and* in a table is one number too many; derive the prose
from the table.

## 3. Three test premises that were wrong about the algorithm

Not defects in the code — defects in my understanding, which the RFC vectors caught. Each
cost a build-fail cycle and each turned into a pinned test.

**Argon2 variants do not agree "at first".** I wrote a test asserting Argon2d and Argon2id
produce identical early blocks and diverge later. They differ at **block 0**: H0 includes
the type byte, so no two variants share any block at any point. The test was wrong about
the algorithm, and the algorithm was right.

**`m' = 4p·floor(m/4p)`, but H0 commits to the requested `m`.** So at p=1, m=8 and m=11
fill byte-identical memory and produce **different tags**. This one is not a curiosity —
it is an operational trap. An engineer told to "increase the memory cost" changes 8 to 11,
gains nothing, and invalidates every stored hash in the fleet. It presents as every user's
password suddenly being wrong, with no error in any log and no obvious connection to the
one-character config change. It is now a test with a paragraph of explanation attached,
because the test is the only place anyone will encounter this before it happens.

**`Cohort.DailyLoginProbability` is `1 - exp(-1/mean)`, not `1/mean`.** A Poisson arrival
rate, not a reciprocal. For a mean of 1.2 days that is 0.5654 rather than 0.8333 — about
a 32% difference, which propagates into every day count in the migration model. A test
asserting the closed form against the simulation is what keeps it honest: they agree to
0.1 percentage points at one year, and they would not if the rate were wrong.

**The generalisation:** when you implement from a spec, the vectors are not a formality.
Three of my assumptions about Argon2 were wrong, all three were plausible, and none would
have been caught by "does it produce a 32-byte output that looks random".

## 4. The asymmetry I decided not to fix

I wrote a test asserting the bridge rejects credentials for users who are not in the
directory. It failed on two of three paths.

`FromIdToken` looks the subject up and returns `UnknownUser`. `FromFormsTicket` and
`FromWsFederation` do not — they take roles from the credential and never consult the
directory. So a ticket for a deleted user, a terminated employee, or a name the attacker
invented **authenticates successfully** on the legacy paths.

The reflex is to add the check. Three lines, obviously more correct.

I did not, and the reasoning is the part worth writing down. The legacy behaviour is
*faithful* — the original application took its roles from the cookie, which is why forged
tickets carry forged roles. Adding a directory check is a behaviour change that will lock
out anyone whose directory row is missing for an unrelated reason: a replication lag, a
tenant migration, a batch job that deleted rows it should not have. That is an outage, and
it belongs to whoever carries the pager, not to me.

So the test now asserts the asymmetry, with the explanation attached:

```csharp
[Fact] public void OnlyTheModernStackChecksThatTheUserStillExists()
```

plus a second test pinning the consolation prize — an unknown subject gets
`Tenant = "unknown"`, so tenant-scoped resources fail closed. That narrows the hole
without closing it.

It is in `known-limitations.md` and it is finding SEC-6 in the security review.

**The generalisation:** a test can record a decision, not just a behaviour. Pinning
something you have deliberately chosen not to change is the difference between a known
limitation and an undiscovered one — and it means the decision cannot be silently reversed
by someone who reads the code six months from now and thinks it is an oversight.

---

## What the numbers do not tell you

284 tests and 6/6 mutations killed are the headline, and they are the less interesting
half.

The mutations are six, and they were chosen to attack the specific sentences the README
claims to have measured — the segregation-of-duties clause, the deny row, deny-wins
resolution, the missing-`exp` rejection, the Argon2 off-by-one. 6/6 killed means **those
six sentences are verified**. It does not mean the suite would catch an arbitrary defect,
and a mutation score is a sample, not a coverage metric.

The check I would keep if I could only keep one is stage 4: regenerate the report and
demand it comes out byte-identical. Not because byte-comparison is elegant, but because a
results document nobody re-runs decays into a claim, and a claim in a Markdown file is
indistinguishable from one somebody made up. Everything else in this project verifies the
code. Stage 4 verifies the *document* — and the document is what the project is for.
