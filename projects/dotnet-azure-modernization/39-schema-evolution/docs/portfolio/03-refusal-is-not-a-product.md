# A safety tool that says no is a safety tool that gets deleted

There is a predictable arc to introducing a linter that blocks things, and
anyone who has done it has watched every step.

1. The tool refuses a migration on a Tuesday afternoon.
2. The engineer needs the migration to ship this week.
3. The tool has told them the statement is dangerous. It has not told them what
   to do instead.
4. Somebody adds `--no-verify`. Or an allowlist. Or a magic comment.
5. Within a month the escape hatch is in the runbook.
6. The tool is decoration.

The failure is not that the refusal was wrong. The refusal was correct — the
statement really would have locked the table. The failure is that **being right
is not sufficient**. A safety tool has to be more convenient than going around
it, and refusal alone is strictly less convenient than doing nothing at all.

So the design constraint is not "how accurate can the rules be". It is: for
every statement the tool flags, it must produce the version that works.

## What that looks like

`ALTER TABLE orders RENAME COLUMN ref TO reference` takes eleven milliseconds
and breaks every currently deployed instance of the application the moment it
commits. The tool refuses it and generates this:

```
EXPAND
  1. ALTER TABLE orders ADD COLUMN reference text
  2. -- CREATE TRIGGER orders_ref_sync BEFORE INSERT OR UPDATE ON orders ...
MIGRATE
  3. -- backfill orders.reference from ref in lag-throttled batches
  4. -- verify: SELECT count(*) FROM orders WHERE reference IS DISTINCT FROM ref
CONTRACT
  5. -- deploy application code reading reference, writing both
  6. DROP TRIGGER IF EXISTS orders_ref_sync ON orders
  7. ALTER TABLE orders DROP COLUMN ref
```

Seven steps, a trigger, a throttled backfill and a full release cycle of
waiting, to replace one statement.

That is not the tool being pedantic. That is the *actual cost* of the change,
made visible before it is scheduled rather than discovered at 3 a.m. The
eleven-millisecond version was never cheap; it was cheap for the database and
expensive for everyone else, and the expense was deferred until it landed on a
different team.

## Feeding the output back into the input

The property is: **flagging and rewriting coincide exactly.** Every statement
the linter flags gets a plan; no clean statement gets one; every generated plan
passes the validator and the linter.

The last clause is the load-bearing one, and it is about twenty lines of test:

```go
for _, stmt := range flagged {
    m, ok := Rewrite(stmt, 16)
    if !ok { t.Errorf("%q: flagged with no rewrite offered", stmt) }
    if errs := m.Validate(); len(errs) != 0 { ... }
    for _, f := range m.Lint(16).Findings {
        if f.Severity == lint.Refuse {
            t.Errorf("%q: the tool refuses its own advice", stmt)
        }
    }
}
```

Twenty lines. It found five bugs that the full unit suite did not.

**A seven-step plan for a statement that takes milliseconds.** `ADD COLUMN
status text NOT NULL DEFAULT 'new'` is catalogue-only from PostgreSQL 11. The
guard fired on `NOT NULL` alone and never asked whether the default made it
safe.

**`strings.Replace(raw, "CREATE INDEX", "CREATE INDEX CONCURRENTLY", 1)`.**
`CREATE UNIQUE INDEX` does not contain the substring `CREATE INDEX`. The
replace matched nothing, returned its input, and the generated plan contained
the original blocking build labelled as the safe version.

**The linter refused `ADD CONSTRAINT ... USING INDEX`** — which is the second
half of the fix it recommends in the first half. The rule matched on statement
kind and never read the clause that changes the answer.

**It refused its own transactional rename**, because the parser did not know
the word `BEGIN` and unknown statements default to ACCESS EXCLUSIVE.

**`ALTER COLUMN TYPE` and `SET NOT NULL` had no rewrite at all** — the two most
common risky statements in any real migration, refused with no alternative,
which is precisely the arc at the top of this essay.

Every one of those is invisible from inside a unit test, because a unit test
asks whether a function does what its author thought. These are all bugs in the
*seam*, and the seam has no author.

## The property had to be weakened, once

It originally read "every *refused* statement gets a plan". `SET NOT NULL`
broke it. The linter warns rather than refuses, because on a four-hundred-row
lookup table the scan is over before anyone notices — but there is still a
strictly better five-step form, and tying the rewriter to refusals would mean
withholding it from the one person who bothered to ask.

So the predicate became `Flagged()` rather than `Refused()`: did the tool have
an opinion, not how loudly did it express it.

Weakening a property under pressure is usually how properties die. The
distinction worth drawing is between weakening it to make a failing test pass —
which destroys the information the property was carrying — and discovering the
property was stated at the wrong granularity. Here the test failure was real
and the *tool's behaviour was right*: it should offer a plan for a warned
statement. The property, not the code, was wrong.

That is a judgement call and it should be uncomfortable to make. The way to
keep it honest is that the reasoning is written down in the report next to the
number, so a reader can disagree with it.

## Letting a plan answer back

One rule created a genuine conflict. `rename-breaks-deployed-code` refuses
every column rename, correctly: a rename is atomic in the database and *not*
atomic across a fleet, so every deployed instance referring to the old name
breaks the instant it commits.

But the shadow-column plan *contains* a rename — and the step immediately
before it deploys application code that reads both names. The rule's hazard is
real in isolation and discharged by the plan.

The mechanism is `Step.Discharges []string`: a step names specific rule IDs
whose hazard the plan removes, and must supply a `Justification`. `Lint`
downgrades those findings to `INFO` and appends the justification to the reason
text.

The design constraint is that it **downgrades and annotates, never deletes**. A
safety tool that lets a plan silently remove its own warnings has stopped being
a safety tool. One that lets a plan *answer* them, in writing, in the report,
is doing the job — the reviewer sees the hazard, sees the argument, and can
reject the argument.

It is still a trust boundary, and `docs/known-limitations.md` says so: a future
maintainer who reaches for this to silence an inconvenient rule will find that
it works. That risk was accepted in exchange for not weakening the rule itself,
because the rule is correct and the alternative — making it fire less often —
would have made it wrong everywhere else.

## The thing that is not being claimed

The tool does not apply the rewrite automatically, and that is deliberate.

The rewrite changes when the migration completes. One statement becomes, in the
worst case, two deploys and a backup cycle. That is a scheduling decision with
consequences for other teams, and a tool that makes it silently has exceeded
its authority — which is a different way to lose the argument, and a worse one,
because it happens after the tool has been trusted.

The tool's job is to make the real cost visible early enough to be a choice. It
is the reviewer's job to make the choice.
