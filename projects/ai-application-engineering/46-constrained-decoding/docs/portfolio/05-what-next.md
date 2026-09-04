# 05 — What I would do next, and what I would tell a team adopting this

## If this were going into production

**Serialise compiled automata.** Compilation is 1.4–1.9 s for the larger
benchmark schemas. A service that compiles per request spends more time building
the automaton than generating tokens. The DFA is a flat transition table and a
bit vector; serialising it is an afternoon, and it converts a per-request cost
into a build-time one. This is the highest-value missing piece by a wide margin.

**Replace key-order enumeration with a seen-keys bitmask.** The current
construction enumerates subsets × permutations and caps at 720 alternatives.
Carrying "which properties have been seen" as automaton state is 2^n instead,
which for realistic objects is dramatically smaller and removes the cap and its
fallback entirely. It needs the fragment builder to understand a state variable,
which is a genuine change rather than an addition — but it is the clean fix.

**Adaptive mask strategy.** The data says trie pruning wins 8–18× on sparse
masks and loses 30% on dense ones. A predictor as crude as "was the previous
mask dense" would probably capture most of it. Not done because a wrong
prediction costs more than either method alone, and I did not have enough
schemas to calibrate on — which is itself the reason to do it with real traffic
rather than five benchmark schemas.

**Bound compilation time.** A hostile schema can be slow within the current
limits. There is no internal timeout, so the operational guidance in the security
review is "compile untrusted schemas on a bounded worker". That is a workaround,
not a fix.

## If I had another week on the research question

**Push past bigrams.** The exactness argument requires a computable partition
function, so the model must be finite-state. A trigram or a small HMM would still
be exactly analysable and would let me ask whether distortion grows with model
context length. My guess is that it shrinks — a more confident model has less
mass to misplace — but that is a guess, and the whole point of this project is
not guessing.

**Measure distortion on a real model by a different route.** The exact method
does not extend. But an *upper bound* might: if you can enumerate the valid set
for a small schema, you can score each valid document under a real model with a
few forward passes and compute the true conditional over that set directly, then
compare against the empirical distribution of constrained samples. That is
tractable for small enums and would connect these numbers to a real system.

**Correct for tokenisation ambiguity.** One document had 14 distinct
tokenisations. Even a perfect-lookahead decoder samples over token sequences, not
documents. Marginalising properly is a research question; measuring how much it
matters is not, and I only did the measuring.

## What I would tell a team adopting constrained decoding

**It does what it says.** Syntactic validity is guaranteed, retry loops go away,
and the failure mode where a parse error surfaces three services downstream
disappears. That is a real and large win.

**It is not free, and the cost is invisible.** You are not getting your model's
distribution restricted to valid outputs. You are getting a different
distribution, measured here at up to 1.42 nats of KL divergence, sometimes with a
different most-likely answer. No test will show you this, because everything it
produces is valid. If output *quality* matters — classification labels, routing
decisions, anything where the choice among valid answers is the point — you
should know this is happening.

**Watch what the compiler tells you.** Diagnostics are the difference between
"this schema is enforced" and "most of this schema is enforced". A schema that
falls back to canonical key order, or gets its strings bounded at a default, is
being enforced more narrowly than you wrote it. Read them; do not discard them.

**Decide retry-versus-constrain from your own validity rate.** The crossover
depends on how often your model already produces valid output, which depends on
your prompt. Measure it. At 13.5% single-shot validity, retry cost 18.5× more
tokens here; at low rates it did not terminate at all; at high rates it would be
competitive.

**The dangerous failure is a superset, not a subset.** If your constraint engine
approximates *upwards*, it will certify documents your schema forbids, and they
will flow into code that trusted the schema. Ask any implementation you adopt
which direction it approximates in, and whether it tells you when it does.

## What I would do differently from the start

I would have written the Python bindings earlier. Two of the three real bugs
surfaced from work at a boundary — the ctypes layer forced the buffer-length
question, and building the experiment cases forced the pattern-plus-length case.
Neither would have been found by more C++ unit tests, because both were
blind spots shared by the code and its tests.

I would also have built the independent oracle first rather than alongside. It
found the surrogate bug on its first run. A cross-check against a differently
constructed implementation is worth more than a large number of tests that share
the implementation's assumptions.
