# Security review

This is a library that (a) parses untrusted input, (b) is called across a C ABI
from a memory-safe language, and (c) does unbounded-looking work driven by that
input. All three are security-relevant.

## Threat model

**Assumed trusted:** the vocabulary supplied by the host application, and the
host process itself.

**Assumed untrusted:** the schema text, and any byte string fed to
`cdec_matches` or `cdec_advance_bytes`. In a realistic deployment the schema
often comes from a tenant, a config file, or an API request.

**Out of scope:** the language model, prompt injection, and anything about the
*content* of generated documents. This library constrains syntax; it makes no
claim about semantics. A schema-valid document can still contain hostile text.

## Findings and mitigations

### 1. Unbounded resource consumption from a hostile schema — mitigated

Subset construction is exponential in the worst case. A schema with deeply
nested optional objects and many properties can be written specifically to blow
up the DFA.

Mitigations in place:
- Key-order enumeration is capped (`maxObjectAlternatives`, default 720) with a
  diagnostic on fallback.
- Strings without `maxLength` are bounded by `defaultMaxStringLength`.
- Integers are bounded by `defaultMaxIntegerDigits`.
- Array repetition requires an explicit or defaulted `maxItems`.

Residual risk: the caller can raise these limits, and a sufficiently adversarial
schema within the default limits can still be slow. **Compile schemas from
untrusted sources on a bounded-time worker, not on a request thread.** There is
no internal timeout.

### 2. Memory safety across the C ABI — mitigated

Every entry point in `capi.cpp` is wrapped in a guard that catches all
exceptions, including `std::bad_alloc`, and converts them to an error return. An
exception unwinding into a ctypes frame is undefined behaviour.

All buffers crossing the boundary are caller-owned with an explicit length. The
vocabulary is passed as a blob plus lengths, and negative lengths are rejected.
`cdec_diagnostics` follows the query-then-fill convention so the caller always
allocates enough.

**A real bug was found here.** `cdec_apply_mask` originally derived the mask
buffer length from the logit count. Those differ whenever the model has special
token ids, and when the vocabulary size is an exact multiple of 64 the mask has
no spare bits — so the function read past the end of a caller-allocated buffer.
Fixed by adding an explicit `word_count` parameter and bounds-checking in
`applyMask`. Regression test:
`apply_mask_tolerates_logits_longer_than_the_mask`.

### 3. Malformed UTF-8 and unpaired surrogates — mitigated

The automaton encodes UTF-8 well-formedness structurally, so overlong encodings
and stray continuation bytes are rejected by construction rather than by a
separate validation pass.

**A second real bug was found here**, by a randomised cross-check that generates
strings from the automaton and validates them with an independent JSON parser.
The automaton accepted `"\ude04"` — a lone low surrogate — because it permitted
any four hex digits after `\u`. Unpaired surrogates cannot be encoded in UTF-8
and are rejected by strict JSON parsers, so this would have produced output that
the automaton certified as valid and a downstream parser rejected. Surrogate
pairing is regular (the first two hex digits decide) and is now modelled
explicitly. Regression test:
`schema_string_requires_surrogate_pairing`.

### 4. Silent superset approximation — mitigated by design

The most dangerous failure for this class of library is an automaton that
accepts *more* than the schema, because the resulting document parses, passes
the constraint check, and flows into downstream code that trusted the schema.

Policy: approximations must always be a **subset** of the schema's language, and
anything that cannot be represented exactly is a hard compile error (ADR 0002).

This was not hypothetical. `compileString` returned as soon as it saw a
`pattern`, silently discarding `minLength`/`maxLength` — a superset
approximation. `{"pattern":"[ab]*","maxLength":2}` accepted `"aaa"`. Fixed with
a DFA product construction. Regression tests:
`schema_intersects_pattern_with_length_bounds`,
`schema_intersects_pattern_with_minimum_length`.

### 5. Integer overflow in mask indexing — reviewed, no finding

Mask indices are `size_t`, derived from vocabulary size, and the bit index is
bounds-checked against `mask.size() * 64`. Token ids crossing the ABI are
`int32_t` and are validated against the installed vocabulary size before use.

### 6. No secrets, no network, no filesystem writes — verified

The library performs no I/O of any kind. It opens no files, makes no network
calls, and reads no environment variables except `CDEC_LIBRARY` in the Python
loader (a path override, used only to locate the shared library). There are no
credentials in the repository.

### 7. Denial of service via the Python analysis layer — mitigated

`build_product_graph` is bounded by `max_nodes` and raises rather than
truncating. `enumerate_valid` first computes the path count in linear time and
refuses above `max_sequences`, because the graph can be small and acyclic while
still having more than 10^9 paths — this is a real case, pinned by
`test_enumeration_refuses_exponentially_many_sequences`.

## Practices

- No `panic`-equivalent in library code; failures return status codes with
  messages.
- No dynamic allocation in the mask hot path beyond the cached mask itself.
- Tests are deterministic and seeded; the randomised cross-checks use a fixed
  seed so a failure is reproducible.
- Built with `/W4 /permissive-` and zero warnings.
