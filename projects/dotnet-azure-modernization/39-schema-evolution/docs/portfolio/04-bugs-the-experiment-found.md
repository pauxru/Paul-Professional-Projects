# The bugs the experiment found

Twelve real defects, none found by a unit test.

Every one was found by asking the model a question it had not been built to
answer, and then insisting on an answer that made sense. That is the whole
method, and it is worth spelling out because it is cheap and almost nobody
does it: **write down what you expect before you run it, and when the number
disagrees, assume the code is wrong before you assume the number is.**

The tests came *after* the model in this project. That ordering is usually
presented as a failure of discipline. Here it was the point: tests written
against a model you have just written encode the same misunderstandings the
model does. These bugs were found by writing tests that asked what *should* be
true rather than what *was* true.

---

## 1. An ordinal enum mistaken for a lattice

**Symptom.** A test asserting that PostgreSQL's lock modes are ordered such
that each mode conflicts with everything the weaker modes conflict with — the
"monotone ordering" assumption — failed on 2 of 28 ordered pairs.

**What was actually true.** `SHARE UPDATE EXCLUSIVE` sorts *below* `SHARE` in
the conventional weakest-to-strongest listing, and conflicts with `SHARE`.
`SHARE` is self-compatible; `SHARE UPDATE EXCLUSIVE` is not. So the "weaker"
mode conflicts with something the "stronger" one permits.

**Why it matters.** The conflict relation is not a total order and is not
derivable from the ordering. Any code that reasons "this operation needs a
lock at least as strong as X, so escalate one step" is unsound. The fix was to
replace the false test with an explicit `Dominates(strong, weak)` predicate and
prove that `ACCESS EXCLUSIVE` is the *unique* mode that dominates all others —
which is the real structure, and a much more useful thing to know.

**The general lesson.** A list that happens to be sorted is not a lattice. The
ordering was a presentational convention in the documentation, and the code
read a mathematical claim into it.

---

## 2. Identifier normalisation that discarded meaning

**Symptom.** `plan.Rewrite("ALTER TABLE public.orders ADD COLUMN ...")` emitted
`ALTER TABLE orders ADD COLUMN ...`.

**Cause.** `ident()` stripped schema qualifiers. The reasoning at the time was
defensible: for lock analysis, the table is the table, and carrying `public.`
around adds noise to every comparison.

**Why it is the most dangerous bug in this project.** The normalised name was
not only used for analysis. It was echoed back out into *generated SQL*. So the
tool took a statement explicitly targeting `public.orders` and produced one
targeting whatever `orders` resolves to under the session's `search_path`.
On a database with a `tenant_a` schema ahead of `public` in the path, the
generated migration silently alters the wrong table.

**Fix.** Preserve the qualified name; add `Bare()` and `Schema()` accessors for
the places that genuinely want the unqualified form.

**The general lesson.** Normalisation is lossy by definition, and the loss is
acceptable only for the consumer you had in mind. The moment a normalised value
crosses into a different consumer — especially one that *emits* rather than
*compares* — the loss becomes a defect. Ask not "is this normalisation
correct?" but "who else reads this field?"

---

## 3. A discarded return value

**Symptom.** `ADD COLUMN IF NOT EXISTS` parsed, but `s.IfNotExists` was always
false.

**Cause.** `p.kw("IF", "NOT", "EXISTS")` consumes the keywords and returns
whether it matched. The call was made for its side effect and the result
dropped.

**Why it survived.** The statement parsed correctly and the field was not read
by any rule that had a test. It is the classic shape of a latent bug: correct
enough to pass everything anyone thought to check.

**The general lesson.** A function called for both its effect and its value is
easy to half-use. Go's unused-variable rule does not help here, because there
is no variable.

---

## 4. Line numbers that were all 1

**Symptom.** Every statement in a multi-statement script reported `line 1`.

**Cause.** The splitter recorded `startLine` when it *flushed* a statement,
by which point the newlines between statements had already been counted into
the buffer.

**Fix.** Lazily `mark()` the line on the first non-blank rune of a statement.

**The general lesson.** Position tracking is state that must be captured at the
moment of the event, not reconstructed afterwards. The reconstruction is always
off by whatever happened in between, and "whatever happened in between" is
exactly the whitespace you were not thinking about.

---

## 5. A paren counter that could not count

**Symptom.** `ALTER TABLE t ADD COLUMN n int DEFAULT (x)::int NOT NULL` parsed
with `Default` equal to the entire rest of the statement, and `NotNull` false.

**Cause.** `parseDefaultExpr` tracked parenthesis depth by *token*. The lexer
produces `)::int` as a single token, so the closing paren was never seen as a
bare `)`, depth never returned to zero, and the scanner consumed everything
that followed. The same bug truncated `now() AT TIME ZONE 'utc'`.

**Fix.** Count parens per *rune* rather than per token, and stop at a fixed set
of column-constraint keywords when at depth zero.

**The general lesson.** Tokenisation boundaries are not expression boundaries.
Any code that counts brackets at token granularity is making an assumption
about the lexer that the lexer never promised.

---

## 6. A doc comment that described a different function

**Symptom.** `ADD COLUMN note text DEFAULT 'nowhere'` was refused as having a
volatile default. So was `DEFAULT 'random thoughts'`.

**Cause.** `IsVolatileDefault` was documented as performing a prefix match
against a list of volatile function names. It was implemented with
`strings.Contains`. `'nowhere'` contains `now`; `'random thoughts'` contains
`random`.

**Fix.** Rewrite as a lexical scanner: a function name must be a complete
identifier immediately followed by `(`; bare keywords must match a whole token;
the contents of string literals and quoted identifiers are skipped entirely.

**The general lesson.** This is the second time in this codebase a name said
the opposite of what the code did (see §7). The comment is not documentation of
the code — it is a *claim about* the code, and claims can be false. When a
comment and an implementation disagree, the comment is usually the one that
records what someone intended, which makes it evidence about the bug rather
than a description of the behaviour.

---

## 7. Inferring intent from parser silence

**Symptom.** A plan step containing `ALTER TABEL orders ADD COLUMN x int` — note
the typo — passed validation and lint, and did not appear in the report.

**Cause.** `Migration.Lint` skipped any step whose SQL produced no parsed
statements, on the reasoning that such a step must be a prose note (`-- deploy
the application`). A typo produces the same silence.

**Fix.** An explicit `Step.Manual bool`. `Validate` reports `empty-step` for a
non-manual step that parses to nothing; `Lint` synthesises `ddl.Unknown` for it,
which is treated as ACCESS EXCLUSIVE and refused.

**The general lesson.** "I could not parse this" and "this is not meant to be
parsed" are different facts, and inferring one from the other collapses a
distinction the user relies on. If a category matters, represent it — do not
derive it from the absence of evidence.

---

## 8. A rewriter that emitted the dangerous statement labelled as the safe one

**Symptom.** `CREATE UNIQUE INDEX idx ON t (c)` produced a "safe" plan whose
first step was `CREATE UNIQUE INDEX idx ON t (c)`.

**Cause.**

```go
SQL: strings.Replace(s.Raw, "CREATE INDEX", "CREATE INDEX CONCURRENTLY", 1)
```

`CREATE UNIQUE INDEX` does not contain the substring `CREATE INDEX`. The
replace matched nothing and returned its input unchanged.

**Why it survived.** The generated step was valid SQL and passed structural
validation. Nothing checked that it was *different* from the input, and nothing
checked that it was concurrent.

**Fix.** Reconstruct the statement from the parse, carrying the index body
through verbatim so partial-index predicates survive.

**The general lesson.** `strings.Replace` on a structured language is a bet
that you have enumerated the surface forms. The failure is silent and returns
the original, which is the worst possible default: the plan looks generated and
is not.

---

## 9. The linter refused the fix it recommends

**Symptom.** `ADD CONSTRAINT uq UNIQUE (ref)` was refused, with the fix "build
the index concurrently, then `ADD CONSTRAINT ... USING INDEX`". Feeding that
recommendation back in produced `REFUSE constraint-builds-index-inline`.

**Cause.** The rule matched on statement *kind* and never read the `USING INDEX`
clause — which the parser did not extract either. `ADD CONSTRAINT ... UNIQUE`
and `ADD CONSTRAINT ... UNIQUE USING INDEX` differ by four words and by roughly
the entire risk of the migration: the first builds an index under ACCESS
EXCLUSIVE, the second adopts one that already exists and is catalogue-only.

**Fix.** Parse `UsingIndex`; exempt it in the rule and in `Scans`; emit an
`INFO` instead, because the statement still takes a brief ACCESS EXCLUSIVE lock
and that deserves a note rather than silence.

**The general lesson.** A rule that judges a statement in isolation will be
wrong about statements whose meaning depends on a clause it does not read. And
a safety tool that contradicts its own advice does not just have a bug — it
loses the argument with the team it exists to protect, permanently, the first
time somebody notices.

---

## 10. Unknown-means-dangerous, applied to `BEGIN`

**Symptom.** The shadow-column plan wraps its two renames in a transaction. The
plan failed its own lint with three `REFUSE`s on `BEGIN`, `COMMIT` and
`DROP TRIGGER`.

**Cause.** The parser did not know those statements. Unknown statements default
to ACCESS EXCLUSIVE and `REFUSE`, which is the right default and was here
applied to three statements that take no table lock at all.

**Fix.** Teach the parser transaction control, `SET`, and trigger DDL, and add
`locks.None` for statements that take no table-level lock. `None` is declared
*after* `AccessExclusive` in the enum on purpose, so the eight real modes keep
the indices PostgreSQL gives them and the conflict matrix stays a direct
transcription rather than a transcription plus an offset somebody will get
wrong.

**The general lesson.** A conservative default is still a *claim*, and it can
be false. "Assume the worst about what you do not understand" is correct policy
and produces incorrect output, and the gap between those two is where the tool
becomes unusable.

---

## 11. Ceremony generated for a safe statement

**Symptom.** `ADD COLUMN status text NOT NULL DEFAULT 'new'` produced a
seven-step expand/migrate/contract plan. PostgreSQL 11 and later execute that
statement in milliseconds.

**Cause.** The guard read `if !s.NotNull && !Rewrites(s, version)`. It fired on
`NotNull` alone and never asked whether a non-volatile default made the
statement catalogue-only.

**Why it is not the harmless direction of error.** Over-warning looks
conservative and is not. It is how a tool teaches its users that its output is
noise, and the next time it produces seven steps that genuinely matter, nobody
reads them. False positives have a compounding cost that false negatives do not.

**The general lesson.** "When in doubt, warn" is only safe if doubt is rare.

---

## 12. The two most common risky statements had no rewrite at all

**Symptom.** `ALTER COLUMN ... TYPE` and `SET NOT NULL` — between them, most of
the risky statements in any real migration — were refused with no alternative
offered.

**Cause.** They were simply never implemented, and nothing checked for the gap.
Every unit test asked "does the rewrite for X produce a good plan?", and there
was no test asking "is there an X for which no rewrite exists?"

**Fix.** Implement both: the `CHECK ... NOT VALID` → `VALIDATE` → `SET NOT NULL`
route for the latter (only on PostgreSQL 12+, where a validated `CHECK` lets
the server skip the scan — below 12 the tool declines to offer a plan rather
than offer theatre), and the full shadow-column dance for the former.

**The general lesson.** Coverage tests measure whether the code you wrote runs.
They cannot measure the code you did not write. The property "for every
statement the linter flags, a plan exists" is the check that finds absence, and
absence is the failure mode that testing is structurally worst at detecting.

---

## What the twelve have in common

Nine of the twelve are the same bug in different clothes: **a value that was
correct for the consumer the author had in mind, used by a consumer the author
had not.** The stripped schema qualifier was fine for lock analysis and wrong
in generated SQL. The `Contains` check was fine for well-formed function calls
and wrong inside string literals. The unknown-means-dangerous default was fine
for real DDL and wrong for `BEGIN`. The kind-based constraint rule was fine
until a clause it did not read changed the answer.

The remaining three are absence: a step never implemented, a return value never
read, a category never represented.

Neither class is reachable by testing a function against its own author's
understanding of it. Both are reachable by taking the system's output and
feeding it back into the system's input, which costs about twenty lines and is
the single highest-yield test in this repository.
