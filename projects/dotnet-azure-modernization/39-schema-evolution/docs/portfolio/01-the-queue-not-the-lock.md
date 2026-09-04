# The lock is not the problem. The queue is.

Ask an experienced engineer why a migration took the site down and you will
usually hear some version of "it took ACCESS EXCLUSIVE on a big table". This is
true and it is not the explanation, and the difference between those two things
is worth a great deal of production uptime.

## Two facts that make the folk model unusable

**Almost every `ALTER TABLE` subform takes ACCESS EXCLUSIVE.** Not the scary
ones. All of them. `SET DEFAULT` takes it. `DROP COLUMN` takes it. `ADD COLUMN`
with no default takes it. `RENAME` takes it. If the rule were "avoid ACCESS
EXCLUSIVE", you could never alter a table again — which means the rule, as
stated, is not what anybody actually does. What they actually do is apply an
unstated intuition about which of those statements are "real work", and that
intuition is where the outages come from.

**The lock modes are not a ladder.** They are conventionally listed
weakest-to-strongest, and the list looks like an ordering, and it is not one.
`SHARE UPDATE EXCLUSIVE` sits below `SHARE` in that list and conflicts with it;
`SHARE` is compatible with itself and `SHARE UPDATE EXCLUSIVE` is not. There
are two such inversions among the 28 ordered pairs. `ACCESS EXCLUSIVE` is the
*unique* mode that conflicts with everything — which is a much stronger and
much more useful statement than "it's the strongest one", because it means
every intuition of the form "escalate one step up" is unsound while the
intuition "ACCESS EXCLUSIVE blocks literally everything" is exactly right.

So the mode is not the variable. What is?

## The queue is the variable

PostgreSQL's lock queue is ordered, and that ordering is the entire mechanism.

When a statement requests a lock it cannot have, it waits — and it waits *at the
head of the queue*. Every request arriving after it also waits, whether or not
that later request conflicts with anything currently held. A `SELECT` needing
`ACCESS SHARE`, which is compatible with the `ACCESS SHARE` the long read is
holding, still blocks, because the `ALTER TABLE` is in front of it.

This is not a bug. FIFO ordering is what prevents writers being starved
indefinitely by a stream of readers. But it means one long transaction plus one
short DDL is sufficient to stop all traffic to a table, and it means **the DDL
is not the part that was slow**.

The measurement, from §1 of the report:

| | |
| --- | --- |
| DDL hold time | 3.0 s |
| DDL wait time | 42.6 s |
| Blocked query-seconds | 32,896 |
| Amplification | **10,965×** |
| Longest queue | 1,284 |

Three seconds of held lock. Nearly thirty-three thousand query-seconds of
blocking. The statement did what it was supposed to do, quickly. The cost was
incurred entirely while it was waiting for a forty-five-second `SELECT` that
somebody's analytics dashboard had opened.

## The prediction that was wrong

The obvious follow-up: remove the long read, and the problem should go away.
The prediction written down before the run was that amplification would drop
below 10×.

It measured **132×**.

Working out why produced the model that actually matters. There are two
independent effects:

- **The long read decides how long the DDL waits.** Remove it and the DDL
  acquires its lock almost immediately.
- **The arrival rate decides how many queries pile up behind it once it does.**
  This is untouched by removing the read.

ACCESS EXCLUSIVE blocks every read. At 85 requests per second, a three-second
hold is on its own enough to cost 397 query-seconds. That is 132× amplification
from a statement doing nothing wrong on a table with nothing long-running on it.

The practical reading is precise and slightly uncomfortable: **"we checked,
nothing long-running is on that table" is a mitigation worth 83×, not a safety
guarantee.** It removes the tail. It does not remove the hazard.

## Which knob to turn

Two variables, and they do not have the same exponent. From §3:

| change | factor on blocked query-seconds |
| --- | --- |
| double the arrival rate | ×1.995 |
| double the stall duration | ×4.202 |

Linear in traffic. **Quadratic in the stall.** The quadratic falls out of the
queue directly: doubling the stall doubles how long each blocked query waits
*and* doubles how many of them there are.

This is the whole quantitative argument for `lock_timeout`, and it is much
sharper than the qualitative version. Halving your traffic — moving the
migration to 3 a.m. — halves the damage. Halving the stall *quarters* it. A
`lock_timeout` of 0.5 seconds does not halve the stall; it cuts it from 42.6
seconds to 0.5, and the measurement bears out what the exponent predicts:
33,723 blocked query-seconds falling to 429.5, a **78.5× reduction**.

## And the bill for that

`lock_timeout` is not free, and the cost is not performance. It is that the
migration acquires a probability of **never landing at all**.

A DDL that times out, backs off, and retries will keep timing out for as long
as something is holding the table. §5 sweeps the retry budget against the same
45-second read:

| retries | total patience | landed |
| --- | --- | --- |
| 0 | 0 s | NO |
| 1 | 10 s | NO |
| 2 | 20 s | NO |
| 3 | 30 s | NO |
| 4 | 40 s | NO |
| 5 | 50 s | yes |
| 8 | 80 s | yes |

The boundary sits where it should: a DDL with a short `lock_timeout` lands only
if its retry budget outlives whatever is already holding the table.

That is a design rule, not a tuning knob. If the longest transaction on a table
can run for five minutes, a three-retry loop on a thirty-second backoff is a
migration that reports success to the operator and never actually ran — and a
schema drift incident three weeks later, when the code that assumed the column
exists reaches production.

Note also that blocked query-seconds barely move across that sweep. **Patience
is not paid for in site impact.** It is paid for in wall-clock time and in an
operator's willingness to sit and watch, which is the argument for automating
the retry loop rather than asking a human to re-run the migration when it
fails.

## What this changes about how you review a migration

The folk model asks: *does this statement take a heavy lock?* Nearly always
yes, so the question does not discriminate.

The model the measurements support asks three questions instead:

1. **How long will it hold the lock?** Which is to say: does it rewrite the
   table, or scan it, or only touch the catalogue? This depends on the
   statement *and* on the server version — `ADD COLUMN ... NOT NULL DEFAULT
   'x'` rewrites every row on PostgreSQL 10 and is catalogue-only on 11.

2. **What will it be waiting behind?** Long-running reads, idle-in-transaction
   sessions, an analytics query someone left open. This is a property of the
   traffic, and it is worth 83×.

3. **How much traffic arrives during the window?** This sets the multiplier on
   whatever the first two produce, and it is the only one of the three that
   scheduling can change.

Only the first is a property of the SQL. That is why a linter that reads only
the statement can tell you a great deal about risk and nothing at all about
impact — and why this tool reports lock *classes* rather than durations, and
says so.
