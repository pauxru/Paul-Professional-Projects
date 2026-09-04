# 005. Two report files: one with timings, one that can be byte-compared

**Status:** accepted
**Date:** 2026-03-08

## Context

The output of this project is a set of measurements. That creates an obligation the
project did not have when it was only code: a results document that nobody re-runs decays
into a claim, and a claim in a Markdown file is indistinguishable from a claim someone
made up.

The intended control is simple — regenerate the report in CI and fail if it changed. It
does not survive contact with the content. Two of the findings are *timing* findings:

- the 611x spread between hash formats (ADR 004),
- the timing-channel accuracy, which is derived from measured medians.

Those numbers change on every run, on every machine, under every background load. A
byte-comparison over a document containing them fails constantly, and a check that fails
constantly is deleted within a fortnight — usually by the person who added it.

The tempting fixes are both bad. **Round the timings** until they are stable: this hides
exactly the variance the reader needs in order to judge how much to trust them, and it
still breaks on a different machine. **Compare with a tolerance**: now the harness needs
a Markdown-aware numeric differ, which is a small parser, which is a thing that will have
its own bugs and its own maintenance and will eventually be the reason the check is
turned off.

## Decision

Generate two files from one code path:

- **`docs/results.md`** — the full report. Contains wall-clock timings, the four-way
  classifier accuracies, and the padding overhead. Written for a human. Not compared.
- **`docs/results-stable.md`** — the same document with every timing-derived value
  removed. Divergence counts, monotonicity counterexamples, migration curves, session
  survival, rejection reasons, prediction verdicts. Byte-compared in stage 4 of
  `test.ps1`.

`ReportGenerator.Build(stable: bool)` is the single entry point; there is no second
generator to drift.

Predictions 5–8 depend on timing. In the stable file they are emitted with the verdict
`timing` and the evidence `see results.md`. They are **not dropped**. A scoreboard that
silently omits four rows is worse than one that admits it could not answer them here —
and the count in the header is derived from the rows, so the two cannot disagree.

## Consequences

**The byte comparison means something.** `docs/results-stable.md` is regenerated in stage
4 and compared against the committed bytes. If a divergence count moves, the build fails
and somebody reads the diff. That is the whole purpose: the numbers quoted in the README
and in these ADRs are checked against the code on every run.

Two further checks sit on top of it, because a stable file is not automatically a
*correct* one:

- The headline strings (`| 592 | 592 | 0 |`, `**16**`, `72 counterexamples`) are asserted
  present. A refactor that quietly changes what is reported fails here even if the file
  is internally consistent.
- `escalations, 0 lockouts` is asserted with a backreference. This is the control: if
  some divergences had been lockouts, the "it passes UAT because nobody reports it"
  argument in ADR 002 would collapse, and the sentence would need rewriting rather than
  re-running.

**A whole class of nondeterminism is caught for free.** Dictionary iteration order, a
`DateTime.Now` that crept into a section header, culture-dependent number formatting —
all of these show up as a failing byte comparison. `InvariantGlobalization` is set in
every csproj partly because this check made a culture bug visible.

**Cost: two files to read, and a discipline to maintain.** The stable file is the one
under test, and it is the *less useful* of the two to a human — the timing findings are
among the most interesting. Readers are pointed at `results.md`, which is the one nothing
verifies. That asymmetry is uncomfortable and is the honest trade: the alternative was
one file that verified nothing.

**A second cost:** anything timing-derived is invisible to the harness. If the timing
channel measurement broke entirely, stage 4 would pass. The behavioural tests in
`Auth.Tests` cover the mechanism; the *numbers* are only covered by rerunning the report
and reading it.

## Alternatives considered

**One file, timings rounded to a stable bucket.** Rejected: buckets wide enough to be
stable across machines are wide enough to hide the finding.

**One file, compared with a numeric tolerance.** Rejected: requires a Markdown differ,
which is a parser, which is a maintenance liability that will outlive the check.

**Emit the timings as a separate JSON artefact and compare the Markdown.** Very close to
what was chosen, and arguably cleaner. Rejected only because it splits one document into
two formats and makes `results.md` — the file people actually read — an assembled thing
rather than a generated thing. The `stable: bool` flag achieves the same separation with
one code path and no build step.
