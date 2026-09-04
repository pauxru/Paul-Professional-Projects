# The problem

There is a category of system that companies are genuinely afraid to touch, and
the fear is rational.

`BILLRUN` is two hundred lines of COBOL. It runs at 02:15 every night, reads the
customer master file, applies a service charge and VAT, subtracts credits, and
writes the file back. It has done this since March 1994. The person who wrote it
retired in 2009. The charging rules are not documented anywhere else — the
program *is* the documentation, and nobody has read all of it.

Downstream, four other jobs read that file. One of them, the archive job, has its
own copybook that reads the account key as a twelve-character string. Nobody
currently working at the company knows that.

The business wants this in Python. Everyone agrees it should be. The project has
been proposed three times and cancelled three times, because the honest answer to
"what happens if we get it wrong?" is "we don't know, and we find out in
production, at night, in an unattended batch window".

## What makes it hard is not the COBOL

The formula is arithmetic. Anyone can port it in an afternoon. The parts that
kill projects like this are:

**The file format has no schema.** There are no delimiters, no lengths, no type
tags. A record is 59 to 94 bytes and every byte's meaning comes from a copybook
held somewhere else. Get one offset wrong and every field after it is garbage
that still decodes to plausible-looking numbers.

**"The same value" and "the same bytes" are different questions.** A field can
decode to exactly the right number and re-encode to different bytes, because
COMP-3 has six legal sign nibbles and three legal encodings of zero. The
downstream systems care about bytes.

**The test data is cleaner than the real data.** Any file a migration team
generates for itself contains canonical encodings, because canonical encodings
are what an encoder produces. Thirty years of production data contains
everything: alternate sign nibbles, negative zero, uninitialised fields full of
EBCDIC spaces, values that overflowed their picture and wrapped.

**Nobody can tell you the acceptance criteria**, because "the output should be
the same" sounds unambiguous until you ask it precisely.

## What I built instead of a port

The port is the easy half and it is not where the risk is. So this project is the
*other* half: a harness that can answer the acceptance question with a number.

- A copybook-driven reader that computes every offset from the specification and
  never from a constant.
- A representation-preserving decoder, so that reading and writing a file back
  unchanged produces a byte-identical file — which turns out to be a much
  stronger property than it sounds.
- A generator that produces corpora containing the awkward cases on purpose,
  built independently of the encoder so that round-trip agreement is evidence
  rather than tautology.
- A differ that reports byte equivalence and field equivalence *separately*,
  because those are different questions with different audiences.
- The business logic implemented four ways, so the arithmetic decision is made
  against measurements instead of intuition.

The deliverable of a project like this is not the new code. It is a sentence
somebody can say in a go/no-go meeting and defend:

> *We ran the new implementation over 5,000 records. The output is byte-identical
> to the current system's output in every field except `CM-BALANCE`, which is the
> field the job is supposed to change. Here is the report.*

Everything in this repository exists to make that sentence true and checkable.

Next: [what a clean test file proves](02-what-a-clean-test-file-proves.md).
