# ADR-005: Generate the results document from the code, and test it

## Status
Accepted.

## Context
`docs/results.md` states specific numbers and draws conclusions from them. Two
things go wrong with documents like that.

The first is that the code changes and the document does not, so it becomes wrong
by neglect while still looking authoritative. This is the common one, and it is
not a failure of honesty.

The second is subtler: results get written up *after* the numbers are known, so
every conclusion is one the author already believed. A write-up in which every
prediction was correct is a write-up whose predictions were written last.

## Decision
Two mechanisms.

**The document is code.** `Experiments.Render()` produces the entire file by
running the checker. A test byte-compares it against what is on disk and fails
with the first divergent line. Any change to the checker that moves a number
fails the build.

**Predictions are structurally required.** `Report.Expect(...)` opens a
prediction and `Report.Found(...)` closes it. `Found` without `Expect` throws.
`Render` throws if any prediction is still open. Registering the prediction is
not a convention, it is the only way to record a result.

## Consequences
The document is hard-wrapped at 80 columns so its diffs are line-level and
therefore reviewable.

It is written from C# with an explicit BOM-less UTF-8 encoding, not by shell
redirection -- PowerShell's `Out-File` adds a BOM and CRLF, which would make the
byte comparison pass on one platform and fail on another. Two tests pin this
directly.

The scoreboard is the interesting output: 9 predictions, 3 held, 6 contradicted.
A test asserts that at least four were contradicted, on the grounds that a
perfect record is evidence of writing the predictions afterwards rather than
evidence of insight.
