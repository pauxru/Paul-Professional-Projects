# ADR 0002: Write a parser rather than matching patterns

## Status

Accepted.

## Context

Most DDL safety tooling in the wild works by regular expression. Look for
`ADD COLUMN`, check whether `NOT NULL` appears nearby, warn. This is quick to
write, easy to extend, and requires no theory.

It is also wrong in ways that are hard to see and expensive to discover.

`ALTER TABLE t ADD COLUMN note text DEFAULT 'contains NOT NULL in a string'` is
safe, and a pattern matcher flags it. `ALTER TABLE t ADD CONSTRAINT c UNIQUE
USING INDEX i` is catalogue-only, and a matcher that recognises `ADD CONSTRAINT
... UNIQUE` refuses it — which means the tool refuses the second half of the
fix it recommends in the first half. That bug was in this codebase, and §13 of
the report is the check that found it.

The general shape of the problem is that **safety depends on structure, not on
substrings**. Whether `ADD COLUMN` is safe depends on whether it has a default,
whether that default is volatile, and which PostgreSQL major version is
running. Those are three facts about the parse tree, and a matcher that
approximates them will be wrong on the cases that matter, because the cases
that matter are the unusual ones.

## Decision

Write a real lexer and recursive-descent parser for the DDL subset the tool
reasons about, and make every lint rule a function of the parsed statement.

The parser produces a `ddl.Stmt` with typed fields: `Kind`, `Table`, `Column`,
`Type`, `Default`, `NotNull`, `Concurrently`, `NotValid`, `UsingIndex`. A rule
that wants to know whether a default is volatile asks `IsVolatileDefault(
s.Default)`, and that function is itself a lexical scanner rather than a
`strings.Contains`, because the naive version matched `now` inside the string
literal `'nowhere'` and refused a safe migration.

Statements outside the subset parse to `Kind: Unknown`, and `Unknown` is
treated as ACCESS EXCLUSIVE with a `REFUSE`. The tool never silently ignores
something it did not understand.

## Consequences

**Rules become short and obviously correct.** `add-column-volatile-default` is
four lines and reads like its own description. The complexity moved into the
parser, where it is tested directly.

**The parser's gaps became visible as bugs.** When the plan generator started
emitting `BEGIN; ... COMMIT;` and `DROP TRIGGER`, those parsed as `Unknown` and
the generated plan failed its own lint. A matcher would have ignored them
silently and shipped. This is the failure mode inverted: with a parser, not
understanding something is loud.

**"Unknown means dangerous" has a cost.** Every statement outside the subset is
refused, which for a real deployment would mean a stream of false positives on
`GRANT`, `COMMENT ON`, and every extension-specific statement. The mitigation
is that the subset covers what actually appears in migrations, and the escape
hatch is `Step.Manual` for prose and out-of-band operations. This is disclosed
in `docs/known-limitations.md`.

**Nine of the twelve bugs found in this project were parser bugs or bugs in
code that consumed the parse.** That is not an argument against the parser; a
pattern matcher would have had the same bugs plus the ones it could not
express, and would have failed silently rather than loudly. But it is the
honest accounting: writing a parser moves the errors, it does not remove them.

The three most instructive:

- `ident()` stripped schema qualifiers, so `public.orders` became `orders`.
  Harmless for lock analysis — the table is the table — and catastrophic in the
  rewriter, which echoed the stripped name into generated SQL and thereby
  retargeted the DDL through `search_path`.
- Statement line numbers were all reported as 1, because `startLine` was
  recorded when a statement was flushed rather than when its first non-blank
  character was seen.
- `parseDefaultExpr` counted parentheses by token, and `)::int` lexes as a
  single token, so the closing paren was never matched and the scanner
  swallowed the rest of the statement.

## Alternatives considered

**`pg_query_go` (libpg_query bindings), which embeds PostgreSQL's real
parser.** This is the correct answer for production and was rejected only
because it requires cgo, which is unavailable in this environment, and because
a vendored C parser is not something a reader can inspect in an afternoon.
Recorded as the first upgrade in `docs/known-limitations.md`.

**Parse to a generic tree and query it with XPath-like selectors.** Rejected:
adds a query language between the rule and the fact, and every rule in this
tool is a two-line predicate on three fields. The indirection would cost more
than it saved.

**Pattern matching with a preprocessing pass to strip string literals and
comments.** Rejected: this is a lexer with extra steps, and having written the
lexer, the parser is another 200 lines.
