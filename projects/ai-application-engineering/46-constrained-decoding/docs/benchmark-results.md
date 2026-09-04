# Measured output of cdec_bench

Every number below was produced by this run.

## 1. Schema compilation

| schema | NFA states | DFA states (subset) | DFA states (minimised) | reduction | compile ms |
|---|---|---|---|---|---|
| enum-3 | 28 | 18 | 14 | 22.2% | 0.19 |
| flat-object | 156 | 57 | 49 | 14.0% | 0.78 |
| person | 28746 | 6914 | 1481 | 78.6% | 1353.80 |
| invoice-nested | 8248 | 1087 | 795 | 26.9% | 64.74 |
| classification | 32462 | 10374 | 2201 | 78.8% | 1865.91 |

## 2. Cost of allowing free object key order

| schema | DFA states (any order) | DFA states (declaration order) | multiplier |
|---|---|---|---|
| enum-3 | 14 | 14 | 1.00x |
| flat-object | 49 | 27 | 1.81x |
| person | 1481 | 376 | 3.94x |
| invoice-nested | 795 | 140 | 5.68x |
| classification | 2201 | 556 | 3.96x |

## 3. Mask computation (vocabulary = 30242 tokens, 99141 trie nodes)

| schema | states | trie-pruned us/state | brute-force us/state | speed-up | avg allowed tokens |
|---|---|---|---|---|---|
| enum-3 | 14 | 2.4 | 48.6 | 20.5x | 2 |
| flat-object | 49 | 3.5 | 43.5 | 12.4x | 19 |
| person | 1481 | 97.4 | 69.8 | 0.7x | 1700 |
| invoice-nested | 795 | 5.8 | 48.0 | 8.3x | 68 |
| classification | 2201 | 104.9 | 68.9 | 0.7x | 1855 |

## 4. Per-state mask cache during decoding

| schema | documents | decode steps | distinct states | cache hit rate | mask us/step (amortised) |
|---|---|---|---|---|---|
| enum-3 | 200 | 1046 | 14 | 98.85% | 0.020 |
| flat-object | 200 | 3300 | 49 | 98.59% | 0.037 |
| person | 200 | 6940 | 281 | 96.05% | 12.666 |
| invoice-nested | 200 | 15406 | 781 | 94.98% | 0.378 |
| classification | 200 | 8318 | 362 | 95.73% | 16.711 |

## 5. Mask deduplication and eager precomputation

| schema | DFA states | distinct masks | dedup ratio | naive KB | deduped KB | precompute ms |
|---|---|---|---|---|---|---|
| enum-3 | 14 | 13 | 1.08x | 51.7 | 48.1 | 0.0 |
| flat-object | 49 | 30 | 1.63x | 181.1 | 111.1 | 0.1 |
| person | 1481 | 138 | 10.73x | 5472.8 | 515.7 | 144.7 |
| invoice-nested | 795 | 65 | 12.23x | 2937.8 | 243.3 | 5.1 |
| classification | 2201 | 152 | 14.48x | 8133.4 | 570.3 | 230.2 |

All invariants held: trie-pruned masks matched brute force at every state, and no masked decode ever reached a dead state.
