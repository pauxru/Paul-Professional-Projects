# What I would do differently

## Concurrency, and how much it would cost

The largest gap is that the model explores one saga instance. Two concurrent
orders competing for the last unit of stock is a real class of bug and the
checker says nothing about it.

I know roughly what adding it costs, because `results.md` measures growth: the
per-step ratio for a single instance is about 2.3 and declining. A second
instance is closer to squaring the space than multiplying it, which puts a
two-instance model of a four-step saga at around the size of the ten-step
single-instance model I already measured -- and a two-instance six-step saga out
of reach.

So the answer is not "add a second instance". It is symmetry reduction: two
instances differing only by which order they are handling are the same state, and
collapsing them is the standard technique. That is a substantial piece of work
and I would want a specific bug to motivate it, because the honest position today
is that inter-saga concurrency belongs in a test against the *participant* -- does
the inventory service serialise decrements? -- rather than against the
orchestration.

## The declarations are the weak point

The model believes what steps say about themselves. `Idempotent`, `Queryable`,
`CanBeRejected` are inputs, and a wrong input produces a confident wrong answer.

Adding `DeclaredIdempotenceIsFalse` was a partial answer -- it catches a
declaration contradicted by the step's own modelled effect -- and it is partial in
exactly the way that matters least. In the model the effect is a pure function
the checker can apply twice. In production it is an HTTP call, and that is the
case where the declaration is actually at risk.

The version I would build: generate contract tests from the declarations. If a
step is declared idempotent, emit a test that calls the real endpoint twice with
the same idempotency key and asserts the observable state matches one call. The
declaration stops being a promise in a design document and becomes a test in
someone's pipeline. That is the piece that would make this tool useful past the
design review, and it is the obvious next increment.

## The extractor is thinner than the checker

The extractor is a topological sort with a tie-break. It reads declared
reversibility and reads/writes, both of which a human supplies. I had imagined
deriving those from source -- Mono.Cecil over the compiled monolith, inferring
which service each call targets and whether it mutates.

I would still build that, but I would build it second, and I now think the order
matters. The valuable output of the extraction is not the ordering. It is the
sentence *"CapturePayment and Dispatch cannot be moved before the pivot, and here
is why"*, and the one about the notification service's idempotency key changing
where the point of no return sits. Both of those come from the *declarations*,
not from the analysis. Automating the analysis would make the tool easier to
point at a codebase without making its output better.

## Things I would not change

BFS over DFS, for minimal counterexamples. The generated-and-byte-compared
results document -- it caught two stale numbers during development and it is the
only reason I trust the prose. And design mutation, which found the two dead
properties that a passing test suite had been hiding for the entire project.

## The prediction I got most wrong

I expected counterexamples to be long, because the argument for model checking is
usually that bugs hide behind interleavings no human would enumerate. Every
counterexample here is six transitions or fewer.

That is a better result than the one I predicted, and it changes the pitch. The
case for this technique is not "your bugs are too deep to find". It is "your bugs
are two steps deep and nobody wrote that test, because neither step looks
interesting on its own".
