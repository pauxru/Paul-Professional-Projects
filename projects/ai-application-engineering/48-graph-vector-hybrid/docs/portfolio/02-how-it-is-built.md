# 02 — How it is built

Java 21, no runtime dependencies outside the JDK, JUnit 5 for tests. Roughly 2,500 lines
of production code and 1,400 of tests. The whole thing runs in under two seconds.

## The pieces

| class | what it does | why it is separate |
|---|---|---|
| `Corpus` | 80 documents, 26 entities with name variants, 19 questions with checkable ground truth | the answer key must not be derived from the code being scored |
| `Embedding` | character 4-gram TF-IDF, L2-normalised sparse vectors, cosine | one similarity function, used by both retrieval *and* resolution — that shared use is the project's thesis |
| `Retriever` | brute-force cosine top-k over documents | |
| `Graph` | property graph, BFS with a hop budget and a relation filter, provenance on every edge | |
| `Resolver` | greedy threshold clustering over surface forms | the step that decides what a node *is* |
| `Plan` | start entity, hop sequence, optional reachability guard, count flag | data, so it can be checked by tests |
| `Executor` | runs a plan against a graph | exactly one, shared by both systems |
| `Systems` | builds the gold graph, the top-k subgraph, and the resolved graph | the only place the two systems differ |
| `Score` | set-valued precision/recall/F1, plus `falseAlarm` | |
| `Experiments` | the report, as a program | |

## Three design decisions that carry the project

### The report is a program

`Experiments.run()` returns the markdown. `Main` writes it to `docs/results.md`. The test
suite byte-compares the committed file against a fresh generation, and `test.ps1`
independently checks the CLI path and hashes the output of three separate JVMs.

A results file that has drifted from the code that produced it is worse than no results
file, because it reads exactly like a current one.

The same mechanism enforces intellectual honesty. `Report.expect(id, text)` registers a
prediction; `Report.found(id, held, text)` settles it; `Report.render()` **throws** if
any prediction is still open. There is no code path that produces a document in which an
inconvenient prediction was quietly dropped.

### The two systems differ in one expression

```java
// graph system
Systems.answer(q.id(), gold)

// vector system
Systems.answer(q.id(), Systems.subgraph(retriever.topK(q.text(), k)))
```

That is the entire difference. Same plan, same executor, same scoring. Whatever the
tables show, it is not a difference in engineering effort between two pipelines.

### Guards express the thing retrieval cannot

```java
Plan.of("Ashford Components", Plan.back("SUPPLIES"))
    .withGuard(new Plan.Guard(Set.of("OWNED_BY", "SUBSIDIARY_OF"), 3,
                              "LISTED_ON", "OFAC SDN List"))
```

Read out: *start at Ashford Components, walk SUPPLIES backwards to its suppliers, and
keep only those from which some path of at most 3 ownership edges reaches an entity with
a LISTED_ON edge to the OFAC SDN List.*

That guard quantifies over paths **no document describes**. It is the shape of every
interesting compliance question and the shape no retriever can express, because there is
no text to retrieve that corresponds to it.

The executor splices the guard's own witness path onto the answer, so the result carries
the reason it was selected, not just the fact that it was. A compliance answer that
cannot show why an entity is implicated is not an answer anyone can act on.

## Ground truth that is not circular

The obvious failure mode of a project like this is an answer key derived from the same
code that produces the answers. `CorpusTest` attacks that from four directions:

- **`triplesAreGroundedInTheirText`** — every declared triple's subject and object must
  actually appear in the sentence that asserts them, matched against the entity's
  declared variants. The graph is built from what the text says.
- **`goldGraphIsTheCeiling`** — the perfectly-resolved graph must answer all 16
  non-open questions *exactly*. If the answer key and the graph disagree, every
  comparison in the report is measuring that disagreement.
- **`premisesAreSufficient`** — the subgraph induced by a question's premise list must
  answer it. Without this, §4's "oracle" is not an oracle.
- **`premisesAreNecessary`** — dropping *any* premise must change the answer. This is
  the one that caught a real bug (see essay 04).
- **`noDocumentStatesTheComposedFact`** — no document may mention both the buyer and a
  sanction, or the central claim of the project is false in its own corpus.

## Determinism, deliberately

Every measurement is reproducible bit-for-bit, and two specific hazards are defended
against because both have cost time on earlier projects:

- **`Set.of()` and `Map.of()` randomise iteration order per JVM** via
  `ImmutableCollections.SALT`. Any such collection whose order reaches a rendered table
  is a coin flip that presents as a flaky test. Ordered collections are used wherever
  order can escape, and `test.ps1` hashes three separate JVMs to catch a regression.
- **`-Duser.timezone=UTC` is pinned in the surefire `argLine`**, not set from code,
  because setting a default inside a running JVM does not reach everything that has
  already cached one.

Retrieval ties break by document id rather than relying on sort stability. That code is
currently unobservable — see essay 04, which explains why it is kept anyway and why
`test.ps1` documents it instead of deleting it.

## Verification

Six stages, each able to fail for its own reason: compile, 133 tests, report freshness
(byte comparison), determinism (three JVMs, SHA-256), **mutation** (six single-token
edits to production code, 6/6 killed), and a secrets scan.

Stage 5 is the one that matters. A project whose entire argument is *measure it, don't
assume it* cannot ship a test suite whose sensitivity nobody has checked.
