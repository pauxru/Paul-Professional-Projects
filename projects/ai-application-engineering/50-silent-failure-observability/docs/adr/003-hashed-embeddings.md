# ADR 003 — Hashed character n-grams instead of a real sentence encoder

**Status:** accepted

## Context

The detectors operate on vectors. A production system would produce those with the same
embedding model the retrieval pipeline already runs — `text-embedding-3-small`,
`all-MiniLM-L6-v2`, or whatever the index was built with. This project has no model
weights, no network, and one third-party dependency.

## Decision

`embedding.embed` hashes character 3-grams and word unigrams into 128 signed columns
(blake2b, `digest_size=8`, sign taken from a second hash bit), then L2-normalises. It is
the hashing trick, it is about forty lines, and it has no learned parameters.

## Consequences

**What this buys.** The whole result set is reproducible on any machine with numpy, in 32
seconds, forever. There is no model version to pin, no download to break, no GPU, and no
API key. A reader can re-derive every number in `results.md` rather than take it on trust.
That is worth more here than fidelity, because the object under study is the *detector*,
not the encoder.

**What it costs, precisely.** Hashed n-grams have no semantics. `embed("the claim was
approved")` and `embed("the claim was denied")` are near-identical, because they share
every character 3-gram but three. A real encoder places them far apart. So this
representation is systematically *blind to meaning-preserving-form and
meaning-changing-form* alike — it sees surface form only.

The honest consequence: this project measures **how well each detector converts a given
amount of distributional movement into an alert**, and it measures that faithfully, because
all eight detectors see the same vectors. It does **not** measure how much a real
degradation moves a real encoder's output. Absolute detection delays are therefore
artefacts of the chosen effect sizes; the *ranking* and the *structural* findings are not.

The structural findings survive the substitution because they do not depend on the
representation being semantic:

- Aggregate PSI misses an 8%-of-traffic regression by dilution. Dilution is arithmetic. A
  better encoder makes the affected slice move further, which changes the threshold at
  which dilution wins, not whether it wins.
- Conditioning on topic is immune to a benign mix shift. That is a property of
  conditioning, not of the vectors.
- A CUSUM calibrated on its own reference window false-alarms. That is a property of
  reflected random walks.
- Self-similarity moves *down* when a subpopulation clusters tightly. That one is the
  interesting case, and it is discussed in ADR 004 and in
  `docs/portfolio/01-the-detector-i-nearly-deleted.md`: the direction of the effect is a
  fact about mixtures of clusters, but the *magnitude* (0.3257 → 0.3208) is small enough
  that a semantic encoder could plausibly reverse it. It is flagged as
  representation-dependent in `docs/known-limitations.md`.

**The swap is a one-function change.** `detectors.py` and `evaluate.py` take arrays and
never call `embed`. Replacing the encoder means replacing `embedding.embed` and re-running
`main.py`; nothing else refers to the hashing trick. `test_embedding.py` pins the
properties any replacement must keep (unit norm, determinism, empty text → zeros) rather
than the specific values, except for one pinned hash used to detect accidental algorithm
drift.
