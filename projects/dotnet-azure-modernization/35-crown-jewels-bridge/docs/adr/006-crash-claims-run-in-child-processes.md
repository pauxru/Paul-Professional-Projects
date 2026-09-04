# ADR-006: crash claims run in child processes, and corruption is detected with a guard

**Status:** accepted
**Date:** after the in-process version aborted the test run at test 9 of 140

## Context

Five claims about the 2009 boundary cannot be asserted from inside the process making
them, because the process does not survive:

| probe | outcome | exit |
|---|---|---:|
| `american-negative-steps` | process died | `-1073741819` |
| `null-option` | process died | `-1073741819` |
| `error-probe-with-null-buffer` | process died | `-1073741819` |
| `american-zero-steps` | survived, returned `0.0` for an option worth 0.91 | `0` |
| `batch-short-buffer` | survived, overwrote 63 guard slots | `4` |

The first attempt asserted these in-process, with a `try/catch` around each. That is not
a mistake anyone makes twice: an access violation from unmanaged code is not a catchable
managed exception, the test host died, xUnit reported nothing, and every test scheduled
after it never ran. The run aborted at test 9 of 140 and the summary said nothing was
wrong.

## Decision

Two mechanisms, for two different problems.

### 1. Every crash claim runs in a child process

`Bridge.Report.exe legacy-probe <name>` runs one probe and exits. The test starts it,
reads stdout, waits, and asserts on the exit code. Exit codes are a closed set:

```csharp
Survived = 0, UnknownProbe = 2, SurvivedWithGarbage = 3, SurvivedWithCorruption = 4
```

Anything else means the process was terminated. That is the definition of `Died` -- not
"exit code is 0xC0000005", which would tie the assertion to one particular fault on one
platform.

Two details that are not optional:

- **Read stdout before waiting for exit.** A child that fills the pipe buffer blocks on
  the write while the parent blocks on the exit. For a crashing child, that is exactly
  when the output gets long.
- **A harness self-check.** `The_probe_harness_distinguishes_a_crash_from_a_refusal`
  runs a nonsense probe name and asserts exit code 2. Without it, every crash test passes
  trivially the moment the executable path is wrong, because "child failed to start" and
  "child died" look identical from outside.

### 2. Memory corruption is detected with a guard pattern, not a fault

`batch-short-buffer` writes 64 doubles into a buffer declared to hold 1. It **does not
fault**. 512 bytes of overrun lands inside allocator padding, and the process carries on
having corrupted something that will fail later, elsewhere, in innocent code.

A fault-based test would report this input as *safe*. So the probe over-allocates with
`NativeMemory.Alloc`, fills the space beyond the declared capacity with a known value,
makes the call, and counts how many guard slots survived. The answer is exactly 63 --
deterministic, which is what makes it an assertion rather than an observation.

The guard is `-8.6421357911e300`: an ordinary absurd magnitude, not a signalling NaN. A
signalling NaN can be quietened by the compiler simply on load, and a guard that changes
when it is read is not a guard.

## Consequences

- `Bridge.Report` has five entry modes, three of which exist only to be run as children.
  A console app with modes that are expected to crash is unusual and is the correct
  shape here.
- `LegacyCrashTests` pairs each probe with its in-process hardened counterpart, so each
  claim reads as one statement: same C++, two boundaries.
- The same guard technique is used *in-process* against the hardened DLL
  (`VariantSession.TryPriceBatchWithGuard`), asserting both halves of what a safe
  boundary owes a caller: it declines, **and** it does not write. That test exists
  because mutation testing found it missing -- see ADR-007.
- `[assembly: CollectionBehavior(DisableTestParallelization = true)]` is set. The native
  engine create/destroy counters are process-wide, so parallel execution turns a
  measurement into a flaky test.

## The finding underneath the mechanism

The two probes that *survive* are the reason the project exists. Three of these inputs
kill the process, and those are the ones a 2009 team would have fixed, because they
announce themselves. The zero-step lattice returning `0.0` with a success code, and the
short buffer quietly overwriting 63 slots, are still there fifteen years later --
because nothing ever told anybody.
