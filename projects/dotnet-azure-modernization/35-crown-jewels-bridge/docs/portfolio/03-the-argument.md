# 3. The argument

Twelve predictions, written down before anything was measured. Eleven were wrong.

That ratio is the point of writing them down. A report that measures things and then
explains why the results are what you would expect has not learned anything; it has
rationalised. Predictions committed before the experiment turn "interesting" into a
testable property.

Here are the four that changed how I would advise a client.

---

## P7: you do not have to audit the C++

**Predicted:** making a 2009 C++ library safe to expose means fixing the C++. The
dangerous behaviour is in the engine, so the engine has to be audited and changed --
which is exactly the work the business refuses to authorise.

**Measured:** `engine.cpp` byte-identical in both DLLs. 328 unsafe outcomes against the
2009 boundary, **0** against the hardened one. Not one line of pricing code touched.

This is the headline, and its practical consequence is that the sponsor's constraint and
the engineer's goal were never in conflict. They only appeared to be because both parties
had inherited the same untested belief about where the danger lived.

The scoping consequence is sharper still. "Audit 60,000 lines of numerical C++ nobody
understands" is not a project anyone can estimate, staff, or finish. "Own the 300 lines it
is reached through" is a two-week piece of work with a clear definition of done and a test
suite that proves it. Same risk removed. Different order of magnitude of proposal.

## P6: an exception through a C ABI is not catchable

**Predicted:** a C++ exception escaping through a C ABI into .NET surfaces as some kind
of managed exception -- `SEHException` at worst. Unpleasant, but catchable.

**Measured:** the child process died. Exit code `-1073741819`. No stack trace, no
`finally`, no flush of anything buffered.

Unwinding a C++ exception through a frame compiled as C is undefined behaviour, and on
MSVC/x64 it terminates. There is no managed frame left to catch it in, because the
unwinder destroyed them on the way past.

The reason this one matters disproportionately is that the mitigation is *four lines* --
a `catch (...)` at every entry point -- and the failure it prevents is total. It is the
single highest-leverage thing in the entire boundary, and the folklore ("you'll get an
SEHException") makes it sound like something you can defer.

## P8: the build flags change the number the business books

**Predicted:** recompiling the untouched engine with a newer compiler and faster
floating-point settings does not change what it computes. The source is identical, so the
prices are identical.

**Measured:** 2,818 of 4,000 positions differ. Worst disagreement 602 ULP, relative
6.68E-014. On a 2,000-step American lattice the same option differs by 3 ULP, because the
error compounds over the lattice rather than cancelling.

`/fp:fast` licenses the compiler to reassociate floating-point arithmetic, contract
multiply-add pairs into FMA, and use vectorised transcendentals with different rounding.
None of that is a bug. All of it changes the answer.

The magnitudes are far below anything a desk would notice on a single trade -- **and that
is the problem, not the reassurance.** A modernisation programme that recompiles for speed
and reconciles against the old system will find a steady drip of tiny unexplained breaks,
conclude they are noise, and in doing so lose the ability to distinguish noise from a real
regression. The correct move is to decide *before* parallel run: either pin the flags, or
agree a tolerance in writing and reconcile against it. Discovering this during parallel run
is the expensive way, and it is the usual way.

## P10: same size is not same layout

**Predicted:** if the managed struct and the C struct are the same size, the layout is
right. A mismatch would show up as a wrong size or a crash.

**Measured:** both structs are 56 bytes. Transposing two fields returns status `Ok` and a
price of 5.179541 against a true 4.759422.

The prediction is the check that most people actually write, and it is worse than no check
at all, because passing it feels like verification. Nothing in the type system, the
compiler or the runtime can see this: at the ABI, both are 56 bytes of the right
alignment. The only defence is asserting **every field offset** on both sides.

---

## The one that held

**P2:** batching a portfolio into one call instead of N saves the per-call overhead, so
the win is large for small options and vanishes as the per-option work grows.

It held, and it held for exactly the predicted reason, which makes it the least
interesting result in the report and worth reporting for that reason. A scoreboard where
everything is a surprise is a scoreboard where the predictions were not sincere.

## What the measurements do not establish

Three things this project cannot claim, stated plainly because a report that only lists
its wins is doing marketing.

**That the approach scales to a stateful engine.** The pricer here has no global state, so
"caught the exception and returned an error code" leaves it clean. A library holding a
half-updated global curve would need an engine-level invalidation flag after a caught
exception -- and that cannot be built without touching the engine, which breaks the entire
premise. This is the most important limitation and it is in
`docs/known-limitations.md` for that reason.

**That the numbers port.** 0xC0000005 on unwinding through a C frame, 602 ULP under
`/arch:AVX2`, 63 slots overwritten -- these are properties of MSVC on x64 Windows. The
*shapes* port. The numbers do not.

**That 600 deterministic inputs constitute a search.** The corpus has four shapes, a fixed
seed, and no coverage feedback. Determinism is required -- the report must be byte
reproducible and the crash indices must name the same inputs on every machine -- but it
means the corpus explores what it was told to explore. It is a sample, not a fuzzer.

## The argument in one paragraph

The danger in a legacy native library is concentrated in its boundary, not distributed
through its implementation, and the failures that survive fifteen years of production use
are systematically the silent ones -- because the loud ones got fixed. That combination
means an untouchable library can be made safe by work you are permitted to do, and that
the absence of incidents in its history is evidence about the *kind* of defects it has,
not about whether it has any.

---

*Next: [what the tests caught](04-what-the-tests-caught.md) -- including four defects in
my own hardened boundary.*
