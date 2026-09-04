# 3. The injectivity criterion

The project has one idea. This is it.

## The question

You are writing a verifier for a database migration. The two engines disagree about how to
represent almost everything, so a raw comparison objects to 93% of rows and is useless. You add
canonicalisation rules to normalise the disagreements away.

Each rule you add makes the verifier quieter. **Which of them are removing noise, and which are
removing evidence?**

You cannot answer this by looking at the output, because the two are indistinguishable in the
output -- both produce silence. You cannot answer it by knowing which defects are present,
because if you knew that you would not need the verifier. You need a property of the rule
itself, checkable without reference to the data.

## The answer

**A rule is safe if it is injective: if it never maps two distinct inputs to the same output.**

The argument is short. A rule is a function `f` applied to both sides before comparison. The
verifier reports a difference when `f(source) ≠ f(target)`, and is silent when they are equal.

A false negative -- a real defect the verifier misses -- is exactly the case where
`source ≠ target` but `f(source) = f(target)`. That is precisely the definition of `f` failing to
be injective. If `f` is injective, `f(source) = f(target)` implies `source = target`, so the
verifier's silence about that column is a correct statement about the values.

An injective rule can therefore suppress noise but cannot suppress a defect. A non-injective rule
can do both, and offers no way to tell which it did.

## The same criterion, twice

The property is not just about rules. It is the definition of corruption itself.

A migration destroys information exactly when the mapping from source value to target value is
not injective. `'0000007' -> 7` collapses `'7'`, `'07'`, and `'0000007'` onto one value; the
original is unrecoverable by any means. `10.0000 -> 10` is injective given the column's declared
scale; the original is recoverable, and what changed is representation.

So the criterion appears on both sides of the problem:

- **The migration is safe** if its value mapping is injective.
- **The verifier is safe** if its canonicalisation is injective.

This symmetry is not something I designed. It fell out of taking the definition of corruption
seriously and then noticing that a canonicalisation rule is the same kind of object as a
migration step -- a function applied to values -- and is therefore subject to the same test.

It also has a pleasant corollary: **a canonicalisation rule is safe exactly when, considered as a
migration, it would not be corruption.** The rule that reconciles `'0000007'` with `7` is unsafe
for exactly the reason the schema change that produced them was.

## Applied

| rule | operation | injective | verdict |
|---|---|---|---|
| `numeric` | `10.0000` and `10` -> `BigDecimal 10` | yes | reconciles two spellings of one value |
| `boolean` | `true` and `1` -> `Boolean.TRUE` | yes | reconciles two spellings of one value |
| `identifier` | `Integer 7` and `String "7"` -> `"7"` | yes | reconciles two spellings of one value |
| `trim` | `'OK        '` and `'OK'` -> `'OK'` | **no** | declares two different values equal |
| `nfc` | NFD `café` and NFC `café` -> NFC | **no** | declares two different values equal |
| `casefold` | `'ACME'` and `'acme'` -> `'acme'` | **no** | declares two different values equal |
| `temporal` | epoch millis -> wall clock | **no** | see ADR 004 |

Note what `identifier` does *not* do. The natural implementation parses both sides as numbers,
which is what makes the account column quiet and is exactly the operation the criterion calls
corruption. `identifier` uses `String.valueOf` instead: it reconciles `Integer 7` with `"7"` and
*refuses* to reconcile either with `"0000007"`. That refusal looks like a bug until you have the
criterion, and it is the whole reason the leading-zero defect is visible at all.

## Does it predict anything?

A criterion that only rationalises what you already decided is worthless. So the report measures
it against something independent: the **blindfold matrix**.

For each rule and each of five planted defects, does removing that rule recover detections that
were previously missed, without costing precision? If so, the rule was blinding the verifier.

The measurement is made by running the code. The injectivity claim is made by inspecting the
rule. They are computed by entirely separate paths.

**Result: no injective rule appears anywhere in the blindfold matrix.** Every measured blindfold
belongs to a non-injective rule. `casefold` is the worst -- it costs 24 of 26 available
detections, and it costs them silently, because removing it changes nothing about the verifier's
output on the real data.

The converse does not hold and is not claimed. `trim` and `temporal` are non-injective and never
blinded anything here, which is a fact about this corpus -- no planted defect happens to hide
behind trailing whitespace -- and not evidence that those rules are safe.

So the criterion is used in one direction: **injective implies safe**. That is enough to be
useful. It reduces "argue about seven rules" to "argue about three", and the three that remain
each get a written decision with the cost stated (ADR 004 is the argument for shipping
`temporal` anyway).

## A second, independent arrival at the same set

Section 9 runs a completely different procedure: plant five defects, and require the verifier to
be *observed catching them* before its silence counts as evidence. Mutation testing, with no
reference to injectivity at all.

The only configuration that catches every applicable control is `{numeric, boolean, temporal,
identifier}` -- the injective rules, plus the documented exception.

Two lines of reasoning, one from a definition and one from an experiment, converging on the same
rule set. That convergence is the strongest thing the project has to offer, because neither
argument knows about the other.

## What it does not solve

The criterion is about *values*. The collation hazard is not a value-level defect at all: the
source's `IGNORECASE` folds `Äpfel` and `äpfel` together, the target's `NOCASE` does not, and
every row is byte-identical. `COUNT(DISTINCT name)` returns 26 and 27. What was destroyed is a
relation among values, not a value.

Injectivity catches it in principle -- the mapping from *constraint-satisfying table states* to
target states is not injective -- but no rule-level check will, because there is no rule
involved. It needs a set-level check, and section 5 adds one. Then three of the five defective
migrators defeat that check too, by compensating error: their own damage merges a different pair
of names, cancelling the divergence back to 26 = 26.

Which is a fair summary of the whole subject. Every check you add is defeated by something, and
the useful question is never "is this check correct" but "what is this check's silence worth".
