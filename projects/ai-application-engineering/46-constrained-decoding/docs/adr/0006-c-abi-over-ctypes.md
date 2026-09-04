# ADR 0006: a narrow C ABI called from ctypes, not a compiled Python extension

**Status:** accepted

## Context

The core is C++20. The analysis and experiment layer is Python. Something has to
bridge them. The usual answer is pybind11 or nanobind: ergonomic, type-safe,
and it makes the Python package depend on a C++ compiler at install time and on
the exact CPython ABI it was built against.

## Decision

Expose a **narrow C ABI** (`src/bindings/capi.h`) and call it from Python with
`ctypes`.

Everything crossing the boundary is a primitive or a pointer to caller-owned
memory. No C++ types, no templates, no ownership transfer except through the
explicit `cdec_create` / `cdec_destroy` pair.

## Consequences

The Python package has no build step and no compiler dependency. It loads a
`.dll`/`.so` and works.

Every entry point is wrapped in a guard macro that catches all exceptions and
converts them to an error return. A C++ exception unwinding into a ctypes frame
is undefined behaviour, and the failure is a crash with no diagnostic, so this is
not defensive programming — it is a correctness requirement of the boundary.

The vocabulary is passed as one concatenated blob plus an array of lengths,
rather than an array of pointers. Marshalling 30,000 individual `char*` values
through ctypes is slow and error-prone; a blob is one allocation.

The cost is that argument types must be declared by hand in `_ffi.py`, and a
mistake there does not raise — it corrupts memory. That is why
`tests/python/test_ffi.py` exercises every entry point including the failure
paths, and why it contains a test for embedded NUL bytes: a length-prefixed ABI
that was accidentally being called as a C string would silently truncate and
wrongly accept.

**This decision found a real bug.** Writing the bindings forced the question
"how long is the mask buffer?", and the answer exposed that
`cdec_apply_mask` derived the mask length from the *logit* count. Those differ
whenever the model has special ids, and when the vocabulary size is an exact
multiple of 64 the mask has no spare bits and the read went off the end of a
caller-allocated buffer. The ABI now carries an explicit `word_count`, and
`apply_mask_tolerates_logits_longer_than_the_mask` pins it.

## Alternatives considered

**pybind11.** Nicer to write, and it would have hidden the bug above rather than
exposing it, because pybind11 would have owned the buffer. Rejected on
distribution grounds: a portfolio project that requires a working C++ toolchain
to `pip install` is a portfolio project nobody runs.

**A subprocess with a JSON protocol.** Trivially safe and completely unusable:
the mask is fetched once per generated token, and a process hop per token is
orders of magnitude more expensive than the computation it wraps.
