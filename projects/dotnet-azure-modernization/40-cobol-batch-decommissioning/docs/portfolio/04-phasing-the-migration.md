# How I'd phase the real migration

This repository is a harness, not a migration. If the engagement were real, here
is the plan I would put in front of a steering committee, and the reasoning
behind its shape.

The organising principle: **the new system must be provably identical before it
is allowed to be different.** Every improvement — better structure, a database, a
service boundary — happens *after* equivalence is demonstrated, never as part of
demonstrating it. Mixing the two is how these projects die: when the output
differs you cannot tell whether it is a port bug or an intended improvement, and
the argument is unresolvable.

## Phase 0 — Recover the specification (2-3 weeks)

Not code. Facts.

- Locate every copybook and get it into version control. Diff the mainframe copy
  against whatever the team believes is current; they will differ.
- Enumerate every reader and writer of the file. This is the phase that finds the
  archive job, and the archive job is why byte equivalence matters. Expect the
  list to be wrong and budget for finding a consumer nobody remembered.
- Extract the arithmetic from the COBOL with a line-number citation for every
  constant. `VAT_RATE = 0.175` in a Python file with no provenance is a liability.
- Get a real production extract, or negotiate hard for one. Everything downstream
  is weaker without it.

**Exit criterion:** a copybook that parses, a list of consumers, and a document
saying what "correct output" means — in bytes, not adjectives.

## Phase 1 — Build the harness (3-4 weeks)

This repository. Reader, generator, differ, reference implementation.

The order matters and it is counter-intuitive: **build the differ before the
reimplementation.** If the differ comes second it will be shaped, unconsciously,
to agree with whatever the new code already does. Written first, against the
format specification, it is an independent judge.

**Exit criterion:** decode the production extract, re-encode it, and get a
byte-identical file. Nothing else in the project is trustworthy until this
passes, and if it does not pass the reason is always interesting.

## Phase 2 — Shadow running (6-8 weeks, and do not compress this)

Both systems run nightly on the same input. The mainframe's output is the one
that goes downstream. The new system's output goes to the differ.

Non-negotiables:

- **Every night, not a sample.** The findings in this repository — negative
  balances at 33%, size errors at 14%, blank-padded fields at 5% — are exactly the
  characteristics a sample misses. Month-end, quarter-end and year-end have
  different data shapes and must each be observed at least once.
- **Any byte difference is a defect until explained.** Not "investigate if
  material". The pressure to wave through a "harmless" representation difference
  will be constant, and one of them is the archive job's key format.
- **Track the differing-record count as the project's headline metric.** It is the
  only number that means anything to a steering committee, and it goes down.

Six to eight weeks is the number because it must span a month-end and ideally a
quarter-end. This phase is where the schedule pressure lands and it is the phase
that must not move.

**Exit criterion:** thirty consecutive nights of zero unexplained differences,
including one month-end.

## Phase 3 — Cutover (one night, with a rehearsed rollback)

- The mainframe job is disabled but not deleted. It stays runnable for at least
  one full quarter.
- The old input file is retained nightly for the same period.
- The rollback is *rehearsed*, not documented: run it once in Phase 2, on a
  Tuesday, with everyone watching.
- The differ keeps running for a quarter, comparing the new output against a
  mainframe run over the same input. That costs almost nothing and it is the only
  thing that catches a defect on a data shape that has not occurred yet.

**Exit criterion:** a quarter-end processed on the new system with the old one
still available.

## Phase 4 — Now improve it (open-ended)

Only now: modular structure, a database instead of a flat file, an API for the
downstream consumers, parallelism.

The `OCCURS DEPENDING ON` finding belongs here rather than earlier. Because
records are variable-length, the file cannot be seeked into or split — record
*n+1* starts wherever record *n* happened to end, and you only know that after
decoding record *n*. Parallelising requires a sequential index-building pass
first. At 360 µs per record a 20-million-record master file is about two hours
single-threaded. That is a Phase 4 problem, and stating it early stops someone
promising a ten-minute batch window in a Phase 1 status report.

## What I would fight about

**"Can we skip the byte-level comparison? Field-level is enough."** No, and this
repository is the evidence: 3,026 of 5,000 records rewritten with zero value
changes, 506 of them visible as a value change to a consumer with a different
copybook. Field-level equivalence is a weaker claim that sounds like a stronger
one.

**"Shadow running is six weeks of nothing happening."** It is six weeks of the
only real testing this project will ever get. Every week removed is a data shape
nobody sees until production.

**"Let's improve the structure while we're in there."** Every improvement made
before equivalence is proven turns a diff into an argument. Improve afterwards,
with a differ still running and a mainframe still switched on.

**"The test extract passed, ship it."** See
[the bug that passes testing](03-the-bug-that-passes-testing.md). The extract
passing is not evidence of correctness; it is evidence that the extract lacks the
characteristic the defect is correlated with.

## What I'd build next in this repository

In rough order of value per day spent:

1. **Streaming.** Everything takes `bytes`. Real master files do not fit in
   memory, and the fix is a `Iterator[bytes]` boundary, not a rewrite.
2. **RECFM=VB support** — block and record descriptor words. Any real dataset
   transferred off a mainframe has them, and they must be stripped before the
   copybook applies.
3. **Multi-layout files.** A record-type discriminator byte selecting between
   copybooks is extremely common and is a genuine structural addition, not a
   parser tweak.
4. **`SIGN IS SEPARATE` and group-level `USAGE`.** The two silent-wrong-answer
   risks in [known-limitations](../known-limitations.md). Both change field
   lengths, so both corrupt every offset after them.
5. **A property test over generated copybooks**, not just generated records:
   random nesting, `OCCURS`, and `REDEFINES`, asserting that the layout engine and
   the generator agree on length. That would have caught the `REDEFINES`
   length bug earlier than the hand-written test did.
