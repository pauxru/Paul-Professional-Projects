# Known limitations

These are ordered by how likely they are to matter.

## One orchestrator, one saga instance

The model explores a single execution of a single saga. Two concurrent orders
competing for the last unit of stock are not explored at all.

This is a real class of bug -- lost updates, write skew, phantom reservations --
and it is out of scope rather than solved. Adding a second instance multiplies
the state space by roughly the square of the first, which the growth measurements
in `results.md` suggest would cap the technique at three or four steps.

The mitigation in practice is that concurrency between saga instances is usually
a property of the *participants* (does the inventory service serialise
decrements?) rather than of the orchestration, and belongs in a different test.

## Exhaustive means exhaustive within the crash budget

The state space is finite only because orchestrator crashes are capped. The
default is one.

`results.md` shows this is not a formality: the defective v4 saga gains new,
genuinely distinct violations at budget 2 and again at budget 3. Any statement of
the form "v4 has three bugs" is really "v4 has three bugs that fit in one crash".

The correct reading of a clean run is **"no counterexample exists with at most N
crashes"**, and N is printed.

## It checks a design, not an implementation

Steps are pure functions of a small integer state vector. Declarations --
`Idempotent`, `Queryable`, `CanBeRejected` -- are believed.

So the tool says: *if* the payment service's capture endpoint is idempotent, the
saga is sound. Whether it actually is remains a question about someone else's
code.

This is a smaller gap than it sounds and a real one. It converts an unexamined
assumption into a written, checkable claim that appears in the design and can be
disagreed with in review. It does not verify it. The one place the checker can
catch a false declaration is when the declaration contradicts the modelled effect
itself -- `DeclaredIdempotenceIsFalse` -- which is exactly the case that does not
arise when the effect is an HTTP call.

## The state vector is small integers

Real participants have state the model cannot express: a payment provider's
internal fraud score, a warehouse's slot calendar. Anything whose behaviour
depends on state outside the vector is modelled as nondeterminism -- it can
succeed, be rejected, or time out -- which is sound but coarse. A step that can
only fail on Tuesdays is modelled as a step that can always fail.

Coarse in the safe direction: it admits more behaviour than reality, so a clean
result stays meaningful and a counterexample may occasionally be one reality
cannot produce.

## No timing, no partial failure within a step

Steps are atomic at the participant. A step that half-writes -- committing a row
and failing before publishing an event -- is not representable. That is a genuine
production failure mode and it is out of scope; the participant is assumed to
have solved it internally, typically with an outbox.

## Reordering does not reduce network calls

Measured and reported in `results.md`: the reordered saga needs exactly as many
cross-service handoffs as the source order, because the data dependencies pin the
payment service on both sides of the pivot regardless. Reordering buys
correctness of shape, not fewer round trips. This was a prediction that failed,
and it is stated here so nobody reads the extractor as an optimiser.

## Reproduction hazards on Windows

`dotnet test` and the report generator are deterministic. The one hazard is that
`docs/results.md` must be written with a BOM-less UTF-8 encoding and LF endings;
regenerating it by shell redirection instead of running the CLI will produce a
file that fails `ResultsIntegrityTests` on some platforms and passes on others.
Two tests pin this so the failure is loud.
