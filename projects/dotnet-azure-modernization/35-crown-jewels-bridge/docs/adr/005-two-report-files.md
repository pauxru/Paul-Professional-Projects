# ADR-005: two report files, because determinism and timing are incompatible

**Status:** accepted
**Date:** after the first attempt to byte-compare a report containing benchmarks

## Context

The report is the product. `docs/results.md` states twelve predictions, measures each
one, and records whether it held. If nobody re-runs it, it is a document that claims to
be a measurement, which is worse than an opinion because it looks like evidence.

So `test.ps1` regenerates it and byte-compares against the committed copy. Any drift
fails the build.

That works until the report contains a benchmark. Half these predictions are about
*cost* -- P/Invoke overhead, `SuppressGCTransition`, batching, callback direction -- and
a wall-clock number is different on every run, on every machine, at every CPU
temperature. Byte-comparing a file containing "43.2 ns" fails immediately and forever.

## The options considered

**Round the timings.** Rounding does not make a measurement stable, it makes it stable
*most of the time*, which is the worst outcome: a check that passes on the developer's
machine and fails in CI, or vice versa, teaches the team to ignore it.

**Drop the byte comparison.** Then nothing verifies the report, and the numbers rot
silently the first time the code changes. This is the default state of most generated
documentation and the reason nobody trusts it.

**Compare only some lines.** A pattern-matching comparison that skips lines containing
digits and units. Fragile, and it fails open -- a claim that stops being generated
matches nothing and passes.

## Decision

Generate two files from one run.

**`docs/results.md`** -- everything, including timings, ratios and hardware-dependent
figures. Written for a human. Not compared.

**`docs/results-stable.md`** -- only claims that are exactly reproducible: outcome
counts, prediction verdicts, prices, ULP distances, struct offsets, exit codes. Where a
prediction's evidence is a timing, the stable file says so explicitly rather than
omitting it:

```
**P1 -- CONTRADICTED.** (Evidence is a wall-clock measurement; see `results.md`.)
```

`test.ps1` byte-compares only the stable file.

The routing is a property of the emitter, not a post-processing filter: each `Prediction`
is settled with a `stable` flag, and `Stable(...)` versus `Full(...)` decides which file
a line reaches. A claim cannot accidentally end up in the stable file, and a claim
omitted from both is visible as a gap in the scoreboard, which lists all twelve
regardless.

## Consequences

- The stable file is about half the size and carries every falsifiable claim.
- The scoreboard has a `reproducible?` column reading `exact` or `wall-clock`, so a
  reader can see at a glance which conclusions they can check themselves and which they
  are taking on trust from one machine.
- The split forced an honest question about each prediction: *what exactly would have to
  come out differently for this to be wrong?* Two proposed experiments did not survive
  it and were cut, because their answer was "it depends how busy the machine is". They
  are recorded in `known-limitations.md`.
- `.gitattributes` sets `* -text` at the repository root. Without it, `core.autocrlf=true`
  on a fresh Windows clone rewrites the committed report to CRLF, and the byte comparison
  fails on a file nobody touched. A determinism check defeated by the version control
  system is the most annoying possible false positive.

## The general form

Reproducibility is not a property of a document; it is a property of each individual
claim. Mixing reproducible and non-reproducible claims in one artefact forces a choice
between verifying nothing and verifying badly. Separating them by *kind* costs one extra
file and makes both halves honest.
