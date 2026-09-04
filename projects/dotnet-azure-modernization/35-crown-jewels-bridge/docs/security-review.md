# Security review

Self-review of the trust boundary. The subject of this project *is* a security boundary,
so this document is about where the boundary is, what it assumes, and where it would
fail.

---

## Threat model

The engine is trusted code with untrusted **inputs**. It is not a sandbox: an attacker
who can load an arbitrary DLL into the process has already won, and nothing here
attempts to defend against that.

The realistic adversary is not an adversary at all. It is a corrupt market data feed, a
spreadsheet with a transposed column, a units mistake in a new integration, and a caller
in another language whose struct definition drifted from the header three years ago. All
of these deliver hostile input through an entirely legitimate channel.

## Where the boundary is

`abi.cpp`, compiled with `PJ_HARDENED=1`. Nine entry points. Everything in front of it is
untrusted; everything behind it assumes its inputs are sane.

| control | what it stops |
|---|---|
| null checks on every pointer parameter | dereferencing a null engine, option or output |
| `option_defect()` on every option | NaN, infinity, negative volatility, negative time, out-of-range rate, non-zero `reserved`, implausible moneyness, invalid `kind` |
| `steps` bounds | `std::vector` sized from a negative int; unbounded CPU |
| `out_capacity < count` | writing past the caller's output buffer |
| `!std::isfinite(*out)` on results | returning a non-price with a success code |
| `PJ_TRY` / `PJ_CATCH` on every entry point | a C++ exception unwinding through a C frame |

The last one is not defence in depth. Unwinding a C++ exception through a frame compiled
as C is undefined behaviour, and on MSVC/x64 it terminates the process -- measured, exit
code `-1073741819`, no managed frame left to catch it in. The barrier is four lines of
`catch` and it is the difference between an error code and losing the process.

## Verified, not asserted

Every control above is exercised by `Bridge.Tests` and, more importantly, by two things
that fail when the control is removed:

- **The fuzz corpus** (600 inputs, both boundaries, isolated child processes): 328 unsafe
  outcomes against the 2009 build, 0 against the hardened one.
- **Mutation testing** (`test.ps1` stage 6): six checks deleted one at a time, native
  rebuild each time, suite must go red for all six.

Mutation testing found one control that was *not* verified -- the batch capacity check --
because the only test for it asserted on the managed wrapper's `ArgumentException`, which
is thrown before the P/Invoke. See ADR-007. That is the failure this document would
otherwise have claimed as a control.

## Struct layout is a security control

`PricingOption` is 56 bytes on both sides. Transposing `Volatility` and `Years` produces
status `Ok` and a price of 5.179541 against a true 4.759422. No error, no crash, an
ordinary number that is wrong.

Nothing in the type system, the compiler or the runtime can catch this: at the ABI both
are 56 bytes of the right alignment. Checking `sizeof` alone -- which is the check most
people write -- would have missed it entirely.

So every field offset is asserted on both sides: `static_assert(offsetof(...))` in
`abi.cpp`, and `AbiContract.Verify()` in the host, run from a static constructor before
any call can happen. A `reserved` field that must be zero catches the other direction: a
caller whose struct has grown a field this build does not know about.

## The DLL resolver refuses to guess

`AbiContract.FindNativeDirectory` walks up from the assembly location looking for exactly
`native/build/bin/pricing.dll`. If it is not there, it throws.

It does not fall back to the current directory, `PATH`, or "some `pricing.dll` nearby". A
resolver that loads whichever DLL it happens to find first is a supply-chain problem
wearing a convenience costume, and the convenience is worth nothing -- the failure mode
it avoids is a clear exception at startup, and the failure mode it introduces is loading
an attacker's binary.

`Variants.Open` accepts only the three known names and declines anything else, tested.

## Handle lifetime

`EngineHandle` is a `SafeHandle`. Because `DisableRuntimeMarshalling` is set (ADR-004),
every call site hand-writes `DangerousAddRef` / `DangerousGetHandle` / `DangerousRelease`
in a `try/finally`. The reference count is held across the native call, so a concurrent
`Dispose` on another thread cannot free the engine underneath an in-flight computation.

Use-after-dispose is tested on all six entry points and produces
`ObjectDisposedException`, not undefined behaviour. Double-dispose is a no-op. Native
create/destroy counters are asserted balanced.

`GCHandle` for the progress callback is a **normal** handle, not pinned.
`GCHandleType.Pinned` throws at run time for any type holding references, which a
delegate does -- and it would only throw on the first call that passes a callback, which
is a latent failure in a rarely-taken path. A normal handle is correct: `ToIntPtr`
returns a handle-table token, not an address, and the runtime keeps the target alive
across a compacting GC. Tested by forcing one mid-call.

## What is not defended

- **Malicious DLL substitution.** Out of scope; an attacker with write access to
  `native/build/bin` owns the process.
- **Resource exhaustion by a legitimate caller.** `kMaxSteps` and `kMaxBatch` bound a
  single call. Nothing bounds the rate of calls; that belongs to the host.
- **Side channels.** Pricing timing varies with input. Irrelevant here, potentially not
  irrelevant in a multi-tenant service.
- **The engine's own state.** It has none. A real library with global state would need an
  invalidation flag after a caught exception -- see `known-limitations.md`.

## Secrets

`test.ps1` stage 7 scans all source, docs and scripts for AWS keys, GitHub tokens,
OpenAI-style keys, PEM private key headers, and assigned password literals. Nothing
found; no credentials, connection strings or endpoints exist in this project, which has
no network or filesystem I/O beyond writing its own report.
