# 0001 — The copybook is the specification

**Status:** accepted

## Context

A fixed-width mainframe file contains no delimiters, no lengths, no type tags and
no header. Every byte's meaning comes from a COBOL copybook held somewhere else
entirely — usually in a PDS on the mainframe, sometimes in a Word document,
occasionally only in someone's memory.

There is a tempting shortcut available at the start of a project like this:
eyeball a hex dump, work out where the fields are, hard-code the offsets, move
on. It works on the first file. It is also how you end up with a reader that is
correct for the records you looked at.

## Decision

Parse the copybook. Compute every offset from it. Never accept a literal offset
anywhere in the codebase.

Concretely:

- `parse_copybook` produces a `Field` tree with computed offsets.
- `record_length(root, odo_counts)` is the single source of truth for size.
- `decode_record` walks the tree; it does not slice at constants.
- The generator computes lengths by laying out bytes, and every generated record
  is asserted against `record_length` before it is returned. Two independent
  code paths, checked against each other on every record.

Two consequences that were not free:

**Column 7 is honoured.** A copybook line whose column 7 holds `*` is a comment.
Real copybooks still carry sequence numbers in columns 1-6, so the comment
indicator is not at the start of the line. A parser that only strips lines
beginning with `*` treats commented-out fields as live ones, and every offset
after them is silently wrong. There is a test for exactly this
(`test_sequence_numbered_comment_in_column_seven`).

**`REDEFINES` contributes no length.** A redefining entry overlays a sibling; it
does not follow it. Adding its size to the group total is the classic way to get
every subsequent offset wrong, and it produces a record length that looks
plausible.

## Consequences

Losing the copybook makes the file unrecoverable, and that should be stated in
the project's risk register rather than discovered. In practice the copybook in
the repository is more trustworthy than the one on the mainframe, because this
one is executed by tests.

The cost is that a copybook this parser rejects blocks the pipeline. That is
intentional: [known-limitations](../known-limitations.md) lists what it rejects,
and every one of those is a case where guessing would produce silently wrong
offsets rather than an error.
