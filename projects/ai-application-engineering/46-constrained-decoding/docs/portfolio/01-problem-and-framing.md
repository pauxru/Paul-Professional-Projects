# 01 — Why this problem, and why it is not the problem people think it is

Constrained decoding has a one-line pitch: *compile the schema to an automaton,
mask the illegal tokens, get valid JSON every time.* The pitch is correct. It is
also the entire content of most implementations, and it hides two things that
matter more than the mechanism.

## The first hidden thing: tokens are not characters

The pitch says "mask the illegal tokens" as if legality were a property of a
character. It is not. A language model emits tokens, and a token is an arbitrary
byte string chosen by a compression algorithm with no knowledge of JSON. In a
typical BPE vocabulary, `{"`, `":`, `",` and `"}` are all single tokens. A token
can begin inside a string literal and end after the closing brace.

So the question is not "which characters are legal" but "which of these 30,000
byte strings, run from the current state, never kill the automaton". That
reframing is the whole design:

- the alphabet has to be **bytes**, or tokens have no interpretation (ADR 0001);
- checking every token every step is ~100k byte transitions, so the vocabulary
  becomes a **trie** and dead subtrees get pruned;
- the answer depends only on the **DFA state**, so it caches — measured 95–99%
  hit rates.

None of that is exotic. It is just what falls out of taking the tokenizer
seriously instead of pretending the model emits characters.

## The second hidden thing: masking is not conditioning

This is the part that made the project worth building.

Everyone who deploys constrained decoding believes they are sampling from the
model's distribution restricted to valid documents. They are not. At each step
the decoder zeroes the illegal logits and renormalises **locally** — over the
tokens legal right now, with no knowledge of how much valid probability mass
lies behind each one. A token that is locally attractive but leads into a
cul-de-sac of unlikely valid completions is systematically over-weighted.

The result is a different distribution. Not a slightly different one:

- document-level KL divergence up to **1.42 nats**
- total variation up to **0.55**
- in **4 of 15** measured configurations, the single most likely valid document
  is a *different document* depending on which method you use

And it is undetectable by any normal means, because both methods produce only
valid documents. Your validator passes. Your tests pass. Your output is biased.

## Why the fix is the interesting part

The correction is not mysterious. Weight each candidate token by the total
probability of valid completions behind it, then renormalise. Implemented here,
and asserted to reproduce the exact conditional to 1e-12.

It requires knowing, for every candidate token, the sum of the model's
probability over every valid document that could follow. For a bigram that is a
dynamic program over a few hundred nodes. For a transformer it is a sum over an
exponentially large set with no structure to exploit.

So the useful conclusion is a negative one: **the bias is real, it is
measurable, and it is not fixable at inference time.** That is worth knowing
before you decide constrained decoding is free.

## What that implied for the build

Wanting an *exact* answer dictated almost every other decision.

Exactness means the partition function must be computable, which means a
finite-state model — hence a bigram, hence "no LLM was used", stated loudly
rather than apologised for. It means enumerating the valid set, which means
schemas small enough to enumerate, which means a guard that refuses when the
path count explodes. It means an independent oracle to cross-check the
automaton, which is why there is a hand-written JSON parser in a project that
does not otherwise need one.

A sampled estimate against a real model would have looked more impressive and
proved nothing: you cannot sample your way to a normalising constant over a set
your sampler already excludes.
