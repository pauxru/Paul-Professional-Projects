# Known limitations

What this project does not do, does not prove, and got wrong.

---

## The engine is not a real pricing library

`engine.cpp` is presented as "60,000 lines of crown jewels". It is a few hundred lines
implementing Black-Scholes, a CRR binomial lattice, and a Monte Carlo path simulator,
written in the style of 2009 numerical C++ -- raw loops, `std::vector` sized from
arithmetic on an `int`, no input validation, no exception safety.

The style is authentic and the size is not. Everything the project measures depends on
the *boundary*, and the boundary does not care whether the function behind it is 300
lines or 60,000. But nothing here demonstrates that the approach scales to an engine with
global mutable state, threads, or its own memory pools -- all of which are common in real
libraries of this vintage and all of which would complicate the "just own the boundary"
conclusion.

**What would change the answer:** an engine with process-global state. The exception
barrier in `abi.cpp` catches and returns an error code, leaving the engine in whatever
state the throw left it. For a stateless pricer that is fine. For a library holding a
half-updated global curve, "caught the exception and returned an error" means the next
call returns a corrupt price with `PJ_OK` -- which is the exact failure this project
ranks worst. That case needs an engine-level invalidation flag, and it cannot be built
without touching the engine.

## Windows and MSVC only

By construction. The subject is a Windows-hosted C++ library, its calling convention, and
its behaviour under MSVC's `/fp:fast`. The specific findings -- 0xC0000005 on unwinding
through a C frame, 602 ULP under `/arch:AVX2` -- are properties of this toolchain.

The *shape* of the findings ports. The numbers do not.

## `steps > kMaxSteps` is a capacity decision dressed as a safety check

The hardened boundary refuses lattices above 20,000 steps. That is not a memory-safety
issue -- the allocation would succeed. It is a refusal to occupy a core for an
unbounded time on behalf of a caller who probably made a typo.

It is in the same function as the genuine safety checks and it is a different kind of
thing. A caller who legitimately wants 50,000 steps has no way to ask, and the honest
design would be a configurable limit rather than a constant. It is a constant because a
configurable limit is a second entry point and a second thing to validate, and the
project had a boundary to keep small.

## Two experiments were designed and cut

**Thread-safety of a shared engine handle.** The plan was to call one engine from
sixteen threads and count corrupted results. It was cut because the result would have
been "it depends how busy the machine is": with a stateless engine the true answer is
"no shared state, therefore safe", and the experiment would have measured the scheduler.
An experiment whose outcome varies with machine load cannot settle a prediction, and
ADR-005 requires each prediction to be settleable.

**Cost of the DLL search path.** The plan was to measure `NativeLibrary.Load` against
the default probing logic. Cut for the same reason: it measures the filesystem cache.

Both are recorded here rather than quietly dropped, because a report that only contains
the experiments that worked is a report with a selection bias nobody can see.

## The fuzz corpus is not a fuzzer

It is a deterministic generator of 600 inputs from a fixed seed, with four shapes. It has
no coverage feedback, no corpus minimisation, and no mutation of interesting inputs. A
real fuzzer (libFuzzer, AFL++) would find things this does not.

The determinism is the point -- the report has to be byte-reproducible, and the crash
indices have to name the same inputs on every machine -- but it means the corpus explores
what it was told to explore. The one-field-awkward shape finds the most defects, which
suggests the shapes are doing real work, but "600 cases, 4 shapes" is a sample, not a
search.

## The negative-volatility identity is specific to Black-Scholes

`call(-sigma) == -put(sigma)` falls out of sigma appearing squared in d1's numerator. It
is a genuine and genuinely alarming property of this formula. It is not a general law
about pricing models, and a local volatility or stochastic volatility model would fail
differently. The general claim -- *a corrupt input can produce a plausible, bookable,
undetectable answer* -- is what ports.

## What went wrong during the build

Recorded because the mistakes are more useful than the successes.

**A test that crashed the test host silently discarded the rest of the run.** The first
version of the crash claims ran in-process. The run aborted at test 9 of 140 and reported
nothing. Fixed by ADR-006.

**The hardened boundary refused the most routine input in the book.** `volatility <= 0`
and `years <= 0` were rejected as out of domain. Every option expiring today has
`years == 0`. A boundary whose safety comes from refusing legal input is unusable and
will be switched off by the first person with a deadline. Fixed with `degenerate_price()`,
which takes the limit at the seam.

**Every rejection said the same thing.** `option_is_sane()` returned `bool`, so every
refusal read "option failed domain validation at the ABI boundary". A boundary that
declines cleanly but will not say which of six fields was wrong has moved the problem to
the support queue, not solved it. Replaced with `option_defect()` returning the field
name.

**`strike = 1e300` with `spot = 42` returned exactly `0.0`.** Finite, non-negative,
plausible, and corrupt -- the exact failure class this project ranks worst, sitting in
its own hardened boundary. Fixed with a relative magnitude check.

**A double-free in the harness.** `NativeVariant.Dispose` tested `_module != nint.Zero`
without ever assigning zero, and a `FuzzRunner` that also implemented `IDisposable` over
the same variant disposed it a second time. Nobody wrote `Free(h); Free(h);` -- two
objects each correctly released what each believed it owned. It was intermittent, because
the OS loader reference-counts, so it only threw when the test was run *alone*.

**A mutation survived.** The batch capacity check could be deleted with the whole suite
still green, because the only test for it asserted on the managed wrapper. See ADR-007.

**A convergence test asserted something false.** CRR error was asserted to shrink
monotonically with step count within a fixed parity class. It does not: the gaps go
1.02e-3, 6.70e-4, **6.93e-4**, 4.48e-5, 6.20e-5. The quantity that oscillates is how
close the nearest lattice node sits to the strike, and parity is only a proxy for it.
Replaced with an O(1/N) envelope plus decay across two doublings.
