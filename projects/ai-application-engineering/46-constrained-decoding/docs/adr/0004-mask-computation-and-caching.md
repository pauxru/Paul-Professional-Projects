# ADR 0004: mask computation by trie pruning, cached per DFA state

**Status:** accepted

## Context

At every decode step the engine must produce a bitmask over the whole
vocabulary. The direct implementation runs each token's bytes through the DFA:
O(Σ token lengths) per step, which for a 30k-token vocabulary is roughly 100k
byte transitions every step.

## Decision

Two independent optimisations:

1. **Trie pruning.** Build a trie over the vocabulary once. Walk it depth-first
   alongside the DFA; when the automaton dies on a byte, the entire subtree
   below that byte is unreachable and is skipped.
2. **Per-state caching.** The mask is a function of the DFA state *alone* — not
   of the history that reached it, not of the position in the document. So it
   can be memoised, and during real decoding the same states recur constantly.

## Consequences

Both were measured rather than assumed, and the first one **does not always
win**:

| schema | allowed tokens | trie vs brute force |
|---|---|---|
| enum-3 | few | 17.8× faster |
| flat-object | few | 10.8× faster |
| invoice-nested | few | 8.5× faster |
| person | ~1,700 | **0.7× — slower** |
| classification | ~1,855 | **0.7× — slower** |

When the mask is dense almost nothing prunes, and the trie's pointer chasing has
worse cache locality than a tight linear scan over contiguous token bytes. This
is reported in full in `docs/benchmark-results.md` rather than quietly omitted.

The cache, by contrast, wins unconditionally: measured hit rates during decoding
range from **94.98% to 98.85%**, which is what makes the amortised per-step cost
tolerable even for the dense schemas where pruning loses.

The strongest result was not planned. Many distinct DFA states permit exactly
the same token set, so masks deduplicate hard:

| schema | DFA states | distinct masks | ratio | naive | deduped |
|---|---|---|---|---|---|
| person | 1,481 | 138 | 10.7× | 5,473 KB | 516 KB |
| classification | 2,201 | 152 | 14.5× | 8,133 KB | 570 KB |

That turns eager precomputation of every mask from an obviously bad idea into a
cheap one — 147 ms and 516 KB for the `person` schema — which matters if you
want a hard per-step latency bound rather than an amortised one.

## Alternatives considered

**Brute force with SIMD.** Would likely beat the trie on the dense schemas,
given the measurements above. Not implemented; the cache makes the per-step cost
small enough that the added complexity was not justified. Noted in
`docs/known-limitations.md`.

**Choose trie or brute force adaptively by mask density.** Attractive, and the
data says the crossover is real. Rejected for now because density is not known
until after the mask is computed, so an adaptive scheme needs a heuristic
predictor, and a wrong prediction costs more than either method alone.
