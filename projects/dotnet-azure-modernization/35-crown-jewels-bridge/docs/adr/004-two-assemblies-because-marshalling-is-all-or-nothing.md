# ADR-004: the host is split in two because `DisableRuntimeMarshalling` is assembly-wide

**Status:** accepted
**Date:** early, and not by choice

## Context

.NET 7 added an assembly-level switch:

```xml
<DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>
```

It removes the runtime marshalling subsystem from every P/Invoke in the assembly. Blittable
arguments go straight through; there is no stub, no signature-driven transformation, and
no per-call decision about how to convert anything. For a hot numerical boundary this is
exactly what you want, and it is one of the things this project set out to measure.

The documentation describes it as disabling marshalling of non-blittable types. What is
easy to miss, and expensive to discover, is what else counts as marshalling.

## The problem

**It disables `SafeHandle` marshalling too.**

`SafeHandle` is not a special case in the runtime; it is a marshalled type like any
other. Its guarantees -- reference counting so the handle cannot be freed mid-call,
critical finalisation so it is released even if the process is unwinding -- are
implemented *by the marshaller*, in the generated stub. Remove the marshaller and a
`SafeHandle` parameter is simply not allowed.

So the assembly that wants the fast calling convention cannot use the type that makes
handle lifetime safe. These are the two things the project most wanted together.

## Decision

Split the host into two assemblies.

**`Bridge.Core`** sets `DisableRuntimeMarshalling` and *does* use a `SafeHandle` -- but
every call site hand-writes what the marshaller would have generated:

```csharp
var added = false;
try
{
    handle.DangerousAddRef(ref added);
    status = NativeMethods.pj_price_european(handle.DangerousGetHandle(), &o, &price);
}
finally
{
    if (added) handle.DangerousRelease();
}
```

Verbose, and correct: the reference count is held across the native call, so a
concurrent `Dispose` cannot free the engine underneath it, and the `finally` runs on
every path.

**`Bridge.Legacy`** does not set the switch and holds the DLLs by raw `nint`
deliberately. Its job is to be fuzzed -- a wrapper that prevented the dangerous
observation would defeat the experiment.

## What was tried and abandoned

A generic helper to remove the ceremony:

```csharp
static T WithHandle<T>(EngineHandle h, Func<nint, T> body) { ... }
```

It does not compile. The call sites need the address of a local (`&price`, `&option`),
and C# forbids taking the address of a local captured by a lambda -- `CS1686`. Making it
work requires moving the outputs to fields or heap allocations, which is a worse trade
than the repetition: it adds an allocation to every call on a path whose entire purpose
is to have no per-call cost.

The explicit try/finally is zero-allocation, obvious under a debugger, and impossible to
get subtly wrong in a way that compiles. It stayed.

## Consequences

- Two assemblies, one contract (`AbiContract`) shared between them, verified from both
  sides.
- `InternalsVisibleTo("Bridge.Tests")` so the tests can reach the raw paths.
- `Bridge.Tests` is `AllowUnsafeBlocks`, and `VariantSession` is `sealed unsafe class` --
  the function-pointer properties on `NativeVariant` require `unsafe` at the *call site*,
  not just at the declaration.
- The performance question the switch was adopted to answer got an answer worth having:
  **P5 was contradicted.** The runtime marshaller was predicted to dominate. It does not,
  for a blittable signature -- the transition itself is the cost, and removing the
  marshaller buys much less than the folklore claims. The measurement is in
  `results.md`; the point of writing the prediction down first is that this is a result
  rather than a rationalisation.

## The general lesson

Assembly-wide switches interact with types that are implemented *by* the machinery they
switch off. `SafeHandle` looks like a library type and behaves like a language feature.
The failure mode is not a subtle bug -- it is a compile error, immediately, which is the
good version. The expensive version of this lesson is the one where the interaction is
silent.
