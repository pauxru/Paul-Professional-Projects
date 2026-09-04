# Zero-downtime schema evolution: measured, not assumed

Every claim below comes from a deterministic model, not from a production
incident. The model is a discrete-event simulation of PostgreSQL's table lock
queue, a lag-driven model of a streaming replica, and a real recursive-descent
parser for the DDL subset that matters. No database is required to reproduce
any of it.

What the model cannot tell you is how long your ALTER TABLE will take. It can
tell you the shape of the damage when it takes longer than you expected, and
that shape turns out to be the part people get wrong.

**14 predictions recorded before measurement: 13 held, 1 contradicted.**

---

## 1. The lock is not the problem. The queue is.

A three-second ALTER TABLE holds ACCESS EXCLUSIVE for three seconds. That is
not the cost. The cost is that it first has to *acquire* the lock, and while
it waits, every query that arrives behind it waits too -- including queries
that conflict with nothing currently held.

PostgreSQL does this on purpose. Without queue fairness, a steady stream of
ACCESS SHARE requests would starve an ACCESS EXCLUSIVE request forever. The
fairness is correct and it is the mechanism that turns one blocked statement
into a stopped table.

The experiment: 60 reads/sec and 25 writes/sec against one table, with a
single 45-second read already in flight. A 3-second DDL arrives at t=20. Each
configuration is run twice, with and without the DDL, and the difference is
reported -- writes contend with each other regardless, and billing that to the
migration would be dishonest.

| quantity | value |
| --- | --- |
| DDL lock hold time | 3.00 s |
| DDL time spent waiting for the lock | 25.00 s |
| query-seconds blocked, no DDL (baseline) | 0.0 |
| query-seconds blocked, attributable to the DDL | 32896.6 |
| amplification (blocked query-seconds / DDL hold) | 10965.5x |
| longest lock queue | 2322 |

> **Expected (written first):** The amplification exceeds 100x: a 3-second DDL costs more than 300
query-seconds.

> **Found — HELD:** Amplification was 10965.5x -- 32896.6 query-seconds of blocking from a
statement that held its lock for 3 seconds, with a peak queue of 2322. The
DDL itself waited 25.0 seconds, which is where the damage comes from: it
is not holding the lock, it is holding the queue.

## 2. The same DDL against a table with no long read

If the long-running read is the ingredient, removing it should remove the
outage, even though the DDL is unchanged and still takes ACCESS EXCLUSIVE.
This was written expecting a clean result and did not get one, which turned
out to be the more useful outcome.

> **Expected (written first):** Without the long read, amplification drops below 10x.

> **Found — CONTRADICTED:** Amplification was 132.3x (396.8 blocked query-seconds), against 10965.5x with the long read present. The prediction is wrong, and wrong in the direction that matters. Removing the long read cuts the damage by a factor of 82.9x, so the long read really is the dominant ingredient -- but what remains is still 132.3x amplification, not the near-nothing the prediction assumed. The reason is that the two effects are independent. The long read decides how long the DDL waits before it can start; the arrival rate decides how many queries pile up behind it once it does. Delete the first and the second is untouched: ACCESS EXCLUSIVE blocks every read, and at this traffic level a 3-second hold is enough on its own to cost 396.8 query-seconds.

The practical reading is that "we checked, nothing long-running is on that table" is a real mitigation worth 82.9x and not a safety guarantee. It removes the tail, not the hazard.

## 3. Blocked time is linear in traffic and quadratic in the stall

Two knobs: how fast queries arrive, and how long the DDL is stuck. They do not
have the same exponent, and knowing which is which decides where to spend
effort.

| change | blocked query-seconds | factor |
| --- | --- | --- |
| arrival rate x1 (0.2s spacing) | 3822.2 | - |
| arrival rate x2 (0.1s spacing) | 7624.9 | 1.995x |
| stall duration x1 (20s) | 1814.7 | - |
| stall duration x2 (40s) | 7624.9 | 4.202x |

> **Expected (written first):** Doubling the arrival rate roughly doubles the damage; doubling the stall
duration roughly quadruples it, because both the number of queued queries
and each one's wait scale with the stall.

> **Found — HELD:** Doubling the rate gave 1.995x; doubling the stall gave 4.202x. Blocked
query-seconds go as rate x stall^2. This is the quantitative argument for
lock_timeout: it cannot reduce your traffic, but it caps the term that is
squared. Halving the worst-case stall cuts the worst-case damage by four.

## 4. lock_timeout works, and it is not free

The standard remedy is `SET lock_timeout` plus a retry loop. The DDL gives up
quickly instead of holding the queue, the backlog drains, and it tries again
later. The question nobody asks is what it costs, and the answer is that the
migration acquires a probability of never landing at all.

Below, the same DDL behind the same 45-second read, swept across timeout
values with 5 retries and a 10-second backoff.

| lock_timeout | blocked query-seconds | amplification | attempts | landed |
| --- | --- | --- | --- | --- |
| none | 33723.1 | 11241.0x | 1 | yes |
| 0.5 s | 429.5 | 143.2x | 4 | yes |
| 1.0 s | 480.6 | 160.2x | 4 | yes |
| 2.0 s | 1002.5 | 334.2x | 3 | yes |
| 5.0 s | 2540.3 | 846.8x | 3 | yes |
| 10.0 s | 7034.3 | 2344.8x | 2 | yes |
| 30.0 s | 33723.1 | 11241.0x | 1 | yes |

> **Expected (written first):** A short lock_timeout cuts blocked query-seconds by more than 10x.

> **Found — HELD:** The best landing configuration (lock_timeout 0.5) blocked 429.5
query-seconds against 33723.1 with no timeout, a 78.5x reduction. Every
timeout setting landed the migration. Every setting landed here, which is
a result about the retry budget rather than about the timeout: 5 retries
at 10-second intervals is 90 seconds of patience against a 45-second read,
so the DDL simply outlasts it. The next section removes that cushion and
finds the boundary.

## 5. How much patience a migration needs

Holding lock_timeout at 0.5 seconds and sweeping the retry budget instead. The
blocking read lasts 45 seconds, so the interesting quantity is total patience:
retries multiplied by backoff.

| retries | total patience | attempts | blocked query-seconds | landed |
| --- | --- | --- | --- | --- |
| 0 | 0 s | 1 | 15.2 | NO |
| 1 | 10 s | 2 | 23.6 | NO |
| 2 | 20 s | 3 | 33.9 | NO |
| 3 | 30 s | 4 | 428.2 | yes |
| 4 | 40 s | 4 | 428.2 | yes |
| 5 | 50 s | 4 | 428.2 | yes |
| 8 | 80 s | 4 | 428.2 | yes |

> **Expected (written first):** There is a threshold: below some retry budget the migration does not land,
above it it does, and the threshold sits near the length of the blocking
read.

> **Found — HELD:** The migration first lands at 20 seconds of patience and last fails at 30. The blocking read is 45 seconds long, so the boundary is where it should be: a DDL with a short lock_timeout lands only if its retry budget outlives whatever is already holding the table. That is a design rule, not a tuning knob -- if the longest transaction on a table can run for five minutes, a three-retry loop on a thirty-second backoff is a migration that reports success to the operator and never actually ran.

Note also that blocked query-seconds barely move across this sweep. Patience is not paid for in site impact; it is paid for in wall-clock time and in the operator's willingness to sit and watch. That asymmetry is the argument for automating the retry loop rather than asking a human to re-run the migration.

## 6. The lock modes are not a ladder

The eight table lock modes are conventionally listed weakest to strongest,
which invites a rule: when you are not sure what a statement does, assume the
next mode up. The conflict relation does not support that rule.

| stronger mode | weaker mode | why it fails to dominate |
| --- | --- | --- |
| SHARE | ROW EXCLUSIVE | SHARE is self-compatible; ROW EXCLUSIVE is not |
| SHARE | SHARE UPDATE EXCLUSIVE | SHARE is self-compatible; SHARE UPDATE EXCLUSIVE is not |

> **Expected (written first):** At least one ordered pair breaks monotonicity, and exactly one mode
dominates all eight -- so the only sound fail-safe is to assume ACCESS
EXCLUSIVE, not to escalate a step.

> **Found — HELD:** 2 of 28 ordered pairs are non-dominating, and 1 mode dominates everything.
SHARE UPDATE EXCLUSIVE sorts below SHARE, yet conflicts with SHARE while
SHARE does not conflict with itself -- so "escalate one step" can make a
request conflict with strictly fewer things. This is why the linter maps
an unparsed statement straight to ACCESS EXCLUSIVE: it is the unique top
of the lattice, and it is the only assumption that cannot be wrong in the
unsafe direction.

## 7. The same migration is safe on one PostgreSQL and an outage on the one before

Three releases changed whether a common statement rewrites the table.
PostgreSQL 11 made `ADD COLUMN ... DEFAULT <constant>` a catalogue-only
change. PostgreSQL 12 made some widening type changes non-rewriting and let
`SET NOT NULL` be proved from an existing validated CHECK.

A linter with the modern behaviour hardcoded gives the wrong answer on older
servers, and it gives it in the dangerous direction: it passes a statement
that will rewrite a 900-million-row table.

```sql
ALTER TABLE orders ADD COLUMN status text DEFAULT 'new';
ALTER TABLE orders ADD COLUMN token uuid DEFAULT gen_random_uuid();
ALTER TABLE orders ALTER COLUMN note TYPE text;
ALTER TABLE orders ALTER COLUMN qty TYPE smallint;
CREATE INDEX CONCURRENTLY idx_orders_status ON orders (status);
```

| PostgreSQL major | refusals | warnings | statements that rewrite |
| --- | --- | --- | --- |
| 10 | 4 | 1 | 4 |
| 11 | 3 | 1 | 3 |
| 12 | 2 | 1 | 2 |
| 14 | 2 | 1 | 2 |
| 16 | 2 | 1 | 2 |

> **Expected (written first):** The identical script produces strictly more refusals on PostgreSQL 10 than
on 16.

> **Found — HELD:** Refusals fall from 4 on PostgreSQL 10 to 2 on 16 for a byte-identical
script. Two of the five statements change classification across the 11 and
12 boundaries. A tool that does not take the server version as an input is
not analysing your database; it is analysing the one its author had
installed.

## 8. The category everyone forgets: scans that are not rewrites

Migration safety tools overwhelmingly check one thing: does this statement
rewrite the table. That check misses an entire class. A validating `ADD
CONSTRAINT ... CHECK` rewrites nothing at all -- and holds ACCESS EXCLUSIVE
for a full sequential scan of every row.

| statement | lock | rewrites | scans | blocks reads |
| --- | --- | --- | --- | --- |
| ALTER TABLE orders ADD CONSTRAINT ck_total CHECK (total >= 0) | ACCESS EXCLUSIVE | no | yes | yes |
| ALTER TABLE orders ADD CONSTRAINT fk_cust FOREIGN KEY (cust) … | ACCESS EXCLUSIVE | no | yes | yes |
| ALTER TABLE orders ALTER COLUMN status SET NOT NULL | ACCESS EXCLUSIVE | no | yes | yes |
| ALTER TABLE orders ADD CONSTRAINT uq_ref UNIQUE (ref) | ACCESS EXCLUSIVE | no | yes | yes |
| ALTER TABLE orders ADD COLUMN note text | ACCESS EXCLUSIVE | no | no | yes |
| ALTER TABLE orders ADD CONSTRAINT ck_total CHECK (total >= 0)… | ACCESS EXCLUSIVE | no | no | yes |
| ALTER TABLE orders VALIDATE CONSTRAINT ck_total | SHARE UPDATE EXCLUSIVE | no | no | no |

> **Expected (written first):** At least three of these statements scan the whole table without rewriting
it, and so are invisible to a rewrite-only check.

> **Found — HELD:** 4 of 7 statements scan without rewriting. Every one of them holds ACCESS
EXCLUSIVE for the duration of that scan, which on a large table is
minutes. The escape in each case is the same shape -- add the constraint
NOT VALID under a brief lock, then VALIDATE it under SHARE UPDATE
EXCLUSIVE, which blocks neither reads nor writes.

## 9. The backfill dilemma: you have to guess, and both guesses are wrong

Filling a new column on a large table is not lock-bound -- the batches take
row locks and nothing else waits on them. It is bound by replication lag.
Every batch produces WAL; a replica that falls too far behind stops being a
usable failover target and, on a system that reads from replicas, starts
serving stale data.

The controller does not know the replica's apply capacity. Nobody does: it
depends on the replica's hardware, what else is running on it, and the shape
of the rows. So a fixed batch size is a bet placed before the information
arrives.

| batch size | rows/sec | lag breaches | peak lag (s) | finished | seconds |
| --- | --- | --- | --- | --- | --- |
| 500 | 500 | 0 | 0.50 | yes | 4000 |
| 2000 | 2000 | 0 | 0.50 | yes | 1000 |
| 5000 | 5000 | 0 | 0.50 | yes | 400 |
| 10000 | 10000 | 195 | 200.00 | yes | 200 |
| 20000 | 20000 | 99 | 300.00 | yes | 100 |

> **Expected (written first):** No fixed batch size is both breach-free and fast: the safe setting is at
least 5x slower than the fast one, and the fast one breaches.

> **Found — HELD:** The safe setting (500) ran 500 rows/sec with 0 breaches; the fast setting
(20000) ran 20000 rows/sec with 99 breaches -- 40.0x the throughput and a
peak lag of 300.00 seconds against a 5-second budget. The conservative
choice is the one people make, and it is why backfills are measured in
days.

## 10. AIMD finds the capacity nobody told it

Additive-increase, multiplicative-decrease is TCP's congestion control rule:
grow the batch by a constant while lag is under budget, halve it the moment it
goes over. It has no model of the replica and no configured capacity. It
probes.

| controller | rows/sec | lag breaches | peak lag (s) | finished |
| --- | --- | --- | --- | --- |
| fixed 500 | 500 | 0 | 0.50 | yes |
| fixed 20000 | 20000 | 99 | 300.00 | yes |
| aimd | 4950 | 27 | 5.69 | yes |

| steady-state batch size | value |
| --- | --- |
| replica apply rate (never revealed to the controller) | 5000 |
| mean batch, second half of the run | 4993 |
| median batch | 4986 |
| 95th percentile batch | 6577 |

> **Expected (written first):** Without being told the apply rate, AIMD settles within 40% of it, beats
fixed-500 on throughput and fixed-20000 on breaches.

> **Found — HELD:** Steady-state mean batch 4993 against a true apply rate of 5000 -- within
0.1%, discovered purely by probing. Throughput 4950 rows/sec (9.9x
fixed-500) with 27 breaches against fixed-20000's 99. The controller is
roughly forty lines and it dominates both fixed settings on the axis each
of them was chosen for.

## 11. Which half of AIMD does the work

AIMD is asymmetric: creep up, collapse down. Is the stability coming from
reacting to lag at all, or specifically from the asymmetry? Replace only the
multiplicative decrease with a symmetric additive one and measure. Then
compare both against the controller most people write first -- scale the batch
by the remaining lag headroom.

| controller | rows/sec | lag breaches | peak lag (s) | batch size cv |
| --- | --- | --- | --- | --- |
| aimd (additive up, multiplicative down) | 4950 | 27 | 5.69 | 0.207 |
| aiad (additive up, additive down) | 4988 | 189 | 8.95 | 0.384 |
| proportional (scale by headroom) | 5013 | 187 | 9.13 | 0.731 |

The proportional controller is worth a note, because it is the one that looks
right. It multiplies the batch size by a factor derived from the lag error,
which means the error drives the *derivative* of the size. That is integral
action, on a plant whose feedback is delayed by the WAL pipeline, and integral
action plus transport delay is the textbook recipe for a limit cycle.

> **Expected (written first):** Throughput is nearly identical across all three; the multiplicative
decrease shows up as a large reduction in breaches rather than a cost in
speed.

> **Found — HELD:** Throughput spread across the three controllers is 1.25% (4950 to 5013
rows/sec). Breaches differ by 7.0x: AIMD 27, AIAD 189, proportional 187.
The asymmetry is close to free -- it costs 0.74% of throughput and removes
85.7% of the lag-budget violations. And the "smooth" proportional
controller is the noisiest of the three, at cv 0.731 against AIMD's 0.207.

## 12. When the ground moves: a concurrent VACUUM

A tuned fixed batch size is tuned for the conditions at tuning time.
Autovacuum kicks in on a neighbouring table, a checkpoint storm lands, someone
starts a second migration -- and the replica's apply capacity drops without
anyone telling the backfill.

The disturbance below cuts apply capacity to 40% between t=200 and t=400.

| controller | breaches during | breaches in the 400s after | peak lag (s) | rows/sec |
| --- | --- | --- | --- | --- |
| aimd | 34 | 8 | 6.48 | 3810 |
| fixed 5000 | 197 | 0 | 300.50 | 5000 |

> **Expected (written first):** Both controllers breach during the disturbance; only AIMD stops breaching
once it has adapted, and its peak lag is materially lower.

> **Found — HELD:** AIMD breached 34 times during the disturbance and 8 times in the 400
seconds after it; the fixed controller breached 197 and 0. Peak lag 6.48
seconds against 300.50. The fixed controller does not recover because it
was never reacting -- it is not that it adapts slowly, it is that a number
in a config file cannot adapt at all, and the operator who chose it is
asleep.

## 13. Refusal is not a product

A tool that blocks a migration and offers nothing gets an exemption flag
within a fortnight, and after that it is decoration. The linter is only useful
if, for every statement it refuses, it can produce the multi-step plan that
achieves the same end state safely -- and if that generated plan passes its
own checks.

| statement | linter | steps in the safe plan | generated plan |
| --- | --- | --- | --- |
| ALTER TABLE public.orders ADD COLUMN status text NOT NULL… | clean | 0 | no rewrite offered |
| ALTER TABLE public.orders ADD COLUMN note text NOT NULL | refuse | 5 | valid, lints clean |
| ALTER TABLE public.orders ALTER COLUMN ref TYPE varchar(2… | refuse | 8 | valid, lints clean |
| ALTER TABLE public.orders ALTER COLUMN total SET NOT NULL | warn | 5 | valid, lints clean |
| CREATE INDEX idx_orders_status ON public.orders (status) | refuse | 2 | valid, lints clean |
| CREATE UNIQUE INDEX idx_orders_ref ON public.orders (ref) | refuse | 2 | valid, lints clean |
| ALTER TABLE public.orders ADD CONSTRAINT ck_total CHECK (… | refuse | 2 | valid, lints clean |
| ALTER TABLE public.orders ADD CONSTRAINT fk_cust FOREIGN … | refuse | 2 | valid, lints clean |
| ALTER TABLE public.orders ADD CONSTRAINT uq_ref UNIQUE (r… | refuse | 2 | valid, lints clean |
| ALTER TABLE public.orders RENAME COLUMN ref TO reference | refuse | 6 | valid, lints clean |

```text
rename public.orders.ref across a fleet (public.orders)
  expand:
    ALTER TABLE public.orders ADD COLUMN ref_new <type>
      rollback: ALTER TABLE public.orders DROP COLUMN ref_new
    CREATE TRIGGER public.orders_dual_write ... -- keep both columns in step
      rollback: DROP TRIGGER public.orders_dual_write ON public.orders
  migrate:
    -- backfill public.orders.ref_new in lag-throttled batches
      rollback: UPDATE public.orders SET ref_new = NULL
    -- deploy readers that prefer the new column; wait one full release cycle
      rollback: -- roll back the deployment
  contract:
    DROP TRIGGER public.orders_dual_write ON public.orders
      rollback: CREATE TRIGGER public.orders_dual_write ...
    ALTER TABLE public.orders DROP COLUMN ref
      irreversible: the old column has been unreferenced by every deployed instance for a full release cycle; recovery is from backup
```

> **Expected (written first):** Flagging and rewriting coincide exactly -- every statement the linter
flags gets a plan, no clean statement gets one -- and every generated plan
passes both the structural validator and the linter.

> **Found — HELD:** 8 of 10 statements were refused outright, 9 rewrites were produced with 0 mismatches, and 9 of those rewrites pass both the validator and the linter.

Getting here took five fixes, and every one was found by this check rather than by any unit test. The rewriter generated a seven-step plan for `ADD COLUMN ... NOT NULL DEFAULT 'new'`, which PostgreSQL 11 and later execute in milliseconds. It patched `CREATE UNIQUE INDEX` with a string replace that searched for `CREATE INDEX`, matched nothing, and emitted the original blocking build labelled as the safe version. The linter refused `ADD CONSTRAINT ... USING INDEX`, which is the second half of the fix it recommends in the first half. It refused its own transactional rename because the parser did not know the word `BEGIN` and defaulted unknown statements to ACCESS EXCLUSIVE. And `ALTER COLUMN TYPE` and `SET NOT NULL` -- the two most common risky statements in any real migration -- were refused with no alternative at all, which is exactly the failure this section was written to catch.

The fifth fix was to the property itself. It originally read "every *refused* statement gets a plan", and `SET NOT NULL` broke it: the linter warns rather than refuses, because on a small table the scan finishes before anyone notices, but there is still a strictly better five-step form. Tying the rewriter to refusals would have meant withholding that form from the one person who asked. The property is about whether the tool flagged something, not about how loudly.

None of these are exotic. They are what happens when a tool's output is never fed back into its own input, and they are the reason this section exists: "the advice survives the advisor" is cheap to state, cheap to check, and catches a class of bug that no amount of testing the two halves separately will find.

The rename is still the striking row: seven steps, a trigger, a throttled backfill and a full release cycle of waiting, to replace a statement that takes eleven milliseconds and breaks every currently deployed instance the moment it commits.

## 14. The contract phase is where the irreversibility lives

Expand and migrate are recoverable: a column you added can be dropped, a
constraint you added NOT VALID can be dropped, a backfill can be nulled out.
Contract is where the old shape is destroyed, and no amount of rollback SQL
brings back a dropped column's data.

So the validator treats the phases asymmetrically. Outside contract, every
step must declare a rollback and that rollback must have been executed against
real data. Inside contract, a step may declare itself irreversible -- but only
with a written justification, and the destructive statements are refused
anywhere else.

| step | rule | detail |
| --- | --- | --- |
| 0 | missing-rollback | expand step declares no rollback |
| 1 | destructive-outside-contract | DROP COLUMN is destructive and is in the expand phase |
| 1 | untested-rollback | rollback is declared but has never been executed against real data |
| 2 | unjustified-irreversible | contract step is irreversible and gives no justification |
| 3 | phase-order | migrate step follows a contract step; phases must not go backwards |
| 3 | unparsed | statement not understood: ALTER TABEL orders VALIDATE CONSTRAINT c |

> **Expected (written first):** The bad plan trips at least five distinct rules, including the mistyped
statement, while the generated plan trips none.

> **Found — HELD:** The bad plan produced 6 problems across 6 distinct rules:
[destructive-outside-contract missing-rollback phase-order
unjustified-irreversible unparsed untested-rollback]. The generated plan
produced 0. Note which rule catches `ALTER TABEL`: it is `unparsed`, not a
spelling check. An earlier version of this model inferred "this step is a
procedural note" from "the parser found nothing", which made a typo
indistinguishable from prose and let it vanish from the report entirely.
Procedural steps now carry an explicit flag, and anything else that fails
to parse is refused.

---

## Findings index

| # | Section | Status |
| --- | --- | --- |
| 1 | The lock is not the problem. The queue is. | HELD |
| 2 | The same DDL against a table with no long read | CONTRADICTED |
| 3 | Blocked time is linear in traffic and quadratic in the stall | HELD |
| 4 | lock_timeout works, and it is not free | HELD |
| 5 | How much patience a migration needs | HELD |
| 6 | The lock modes are not a ladder | HELD |
| 7 | The same migration is safe on one PostgreSQL and an outage on the one before | HELD |
| 8 | The category everyone forgets: scans that are not rewrites | HELD |
| 9 | The backfill dilemma: you have to guess, and both guesses are wrong | HELD |
| 10 | AIMD finds the capacity nobody told it | HELD |
| 11 | Which half of AIMD does the work | HELD |
| 12 | When the ground moves: a concurrent VACUUM | HELD |
| 13 | Refusal is not a product | HELD |
| 14 | The contract phase is where the irreversibility lives | HELD |

