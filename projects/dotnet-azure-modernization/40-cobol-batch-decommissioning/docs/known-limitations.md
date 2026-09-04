# Known limitations

This list is longer than the feature list, on purpose. A tool that reads
mainframe files and does not say what it cannot read is more dangerous than no
tool at all, because it will produce a plausible answer for a file it does not
understand.

## The copybook parser

**Rejected outright** (raises rather than guesses — every one of these would
otherwise produce silently wrong offsets):

- `PICTURE` scaling positions (`P`). They contribute to scale but occupy no
  storage, and they are rare enough that supporting them untested is worse than
  refusing.
- Edited pictures — `Z`, `*`, `,`, `.`, `+`, `-`, `CR`, `DB`, `/`, `B`, `0`.
  These appear in report layouts, not interchange records.
- Duplicate field names anywhere in a record (except `FILLER`), because
  `Record["FIELD"]` would be ambiguous.
- `OCCURS DEPENDING ON` whose control field appears *after* the array.
- `OCCURS DEPENDING ON` without a `TO` range.

**Silently unsupported** — these parse, and the result may be wrong:

- `SIGN IS LEADING` / `SIGN IS SEPARATE`. Both are real, both change where the
  sign lives and `SEPARATE` changes the field's length. The clause is ignored and
  a trailing embedded sign is assumed. This and group-level `USAGE` are the two
  most likely sources of a wrong answer in the whole codebase.
- `USAGE` declared on a group and inherited by its children. The declaration is
  ignored and children default to `DISPLAY`.
- `COMP-1` / `COMP-2` (single and double precision floating point).
- `COMP-5` (native binary, no digit-count truncation).
- Level 66 `RENAMES`.
- `BLANK WHEN ZERO`, `JUSTIFIED RIGHT`, `VALUE` clauses.
- `COPY ... REPLACING`.
- Nested `OCCURS DEPENDING ON` (an ODO table inside an ODO table).
- More than one ODO in a record. One is parsed and handled; two are not tested
  and the offsets of the second would be computed from the first's minimum.

## Record handling

- **`REDEFINES` offsets are static.** A redefining entry is decoded at the offset
  assigned at parse time, computed with all `OCCURS DEPENDING ON` at their
  minimum. If a variable-length array precedes a `REDEFINES` in the same record,
  the overlay will be decoded at the wrong place. The corpus here has the
  overlay before the array; a real copybook might not.
- **`REDEFINES` overlays are never written back.** `encode_record` skips them.
  With an overlay, two cells claim the same bytes and there is no general way to
  know which view is authoritative. Skipping is safe *because* the redefined
  field covers the same bytes; it is not a solution to the general problem.
- **Record boundaries come from the copybook only.** RECFM=VB block descriptor
  words and record descriptor words are not handled. A real dataset transferred
  from a mainframe usually has them, and they must be stripped first.
- **No support for a file containing more than one record layout.** Multi-format
  files keyed on a record-type byte are extremely common and would need a
  discriminated-union layer above this one.

## Character data

- Only CP037 and CP500 are implemented. There are dozens of EBCDIC code pages.
- Text fields are round-tripped through Unicode. A byte that maps to a
  substitution character will not survive; `from_text(to_text(b))` is only exact
  for bytes in the code page's printable range. Binary data stored in a `PIC X`
  field — which happens — will be corrupted.
- No DBCS / mixed SO-SI data.

## Arithmetic

- The `BILLRUN` rules are reconstructed here for the sake of the experiment.
  In a real engagement every constant needs a citation back to a line of COBOL.
- Only the `COMPUTE` path is modelled. `ADD`/`SUBTRACT`/`MULTIPLY ... GIVING`
  have their own truncation behaviour.
- The size-error model truncates high-order digits, which matches IBM COBOL
  without `ON SIZE ERROR`. Other compilers differ.

## Performance

- ~360 µs per record to decode, which includes two extra encodes per field for
  the exactness self-check. A 20-million-record master file would take about two
  hours single-threaded.
- **The file cannot be split.** With `OCCURS DEPENDING ON` there is no fixed
  record length, so byte offset *n* cannot be interpreted without decoding
  everything before it. Parallelising requires a sequential index-building pass
  first. This is a property of the format, not of this implementation, and it is
  the single most under-estimated cost in projects like this.

## What is not tested

- Real mainframe files. Everything here runs against a generator whose corruption
  rates were chosen, not measured. The *shape* of the findings is robust; the
  percentages are a property of the corpus.
- Files larger than memory. Everything takes `bytes`.
