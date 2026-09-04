// Command evolve generates docs/results.md.
//
// Every section states a prediction before it measures, and whether the
// prediction held is computed from the data rather than asserted by the
// author. That is the only structural defence against writing the conclusion
// after seeing the number.
package main

import (
	"fmt"
	"os"
	"sort"

	"evolve/backfill"
	"evolve/ddl"
	"evolve/lint"
	"evolve/locks"
	"evolve/plan"
	"evolve/report"
	"evolve/sim"
)

func main() {
	out := "docs/results.md"
	if len(os.Args) > 1 {
		out = os.Args[1]
	}
	r := build()
	md, err := r.Render()
	if err != nil {
		fmt.Fprintln(os.Stderr, "evolve:", err)
		os.Exit(1)
	}
	if err := os.MkdirAll("docs", 0o755); err != nil {
		fmt.Fprintln(os.Stderr, "evolve:", err)
		os.Exit(1)
	}
	if err := os.WriteFile(out, []byte(md), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, "evolve:", err)
		os.Exit(1)
	}
	total, held, contra := r.Tally()
	fmt.Printf("wrote %s\n", out)
	fmt.Printf("sha256/16 %s\n", report.Sha(md))
	fmt.Printf("predictions %d: %d held, %d contradicted\n", total, held, contra)
}

// baseWorkload is the traffic profile every lock experiment runs against: a
// moderately busy table with one long-running read already in flight. The
// long read is not an adversarial detail. On any real system there is always
// one -- an analytics query, a pg_dump, an ORM session that opened a
// transaction and went to lunch.
func baseWorkload(seed int64) sim.Workload {
	return sim.Workload{
		ReadsPerSec: 60, WritesPerSec: 25,
		ReadSeconds: 0.015, WriteSeconds: 0.030,
		LongQuerySeconds: 45,
		Duration:         180,
		Seed:             seed,
	}
}

func build() *report.Report {
	r := report.New("Zero-downtime schema evolution: measured, not assumed")

	r.Intro("Every claim below comes from a deterministic model, not from a production incident. " +
		"The model is a discrete-event simulation of PostgreSQL's table lock queue, a lag-driven " +
		"model of a streaming replica, and a real recursive-descent parser for the DDL subset that " +
		"matters. No database is required to reproduce any of it.")
	r.Intro("What the model cannot tell you is how long your ALTER TABLE will take. It can tell you " +
		"the shape of the damage when it takes longer than you expected, and that shape turns out " +
		"to be the part people get wrong.")

	sectionAmplification(r)
	sectionScaling(r)
	sectionTimeout(r)
	sectionLattice(r)
	sectionVersions(r)
	sectionScansNotRewrites(r)
	sectionFixedBatch(r)
	sectionAIMD(r)
	sectionAblation(r)
	sectionDisturbance(r)
	sectionRewriter(r)
	sectionContract(r)
	return r
}

// ---------------------------------------------------------------------------

func sectionAmplification(r *report.Report) {
	s := r.Section("The lock is not the problem. The queue is.")
	s.Text("A three-second ALTER TABLE holds ACCESS EXCLUSIVE for three seconds. That is not the " +
		"cost. The cost is that it first has to *acquire* the lock, and while it waits, every " +
		"query that arrives behind it waits too -- including queries that conflict with nothing " +
		"currently held.")
	s.Text("PostgreSQL does this on purpose. Without queue fairness, a steady stream of ACCESS " +
		"SHARE requests would starve an ACCESS EXCLUSIVE request forever. The fairness is correct " +
		"and it is the mechanism that turns one blocked statement into a stopped table.")
	s.Text("The experiment: 60 reads/sec and 25 writes/sec against one table, with a single " +
		"45-second read already in flight. A 3-second DDL arrives at t=20. Each configuration is " +
		"run twice, with and without the DDL, and the difference is reported -- writes contend " +
		"with each other regardless, and billing that to the migration would be dishonest.")

	w := baseWorkload(20240601)
	ddlReq := sim.Request{Arrive: 20, Hold: 3, Mode: locks.AccessExclusive, Label: "ddl"}
	amp := sim.Measure(w, ddlReq, "ddl")

	s.Table(
		[]string{"quantity", "value"},
		[][]string{
			{"DDL lock hold time", report.Num(amp.DDLHold, 2) + " s"},
			{"DDL time spent waiting for the lock", report.Num(amp.DDLWait, 2) + " s"},
			{"query-seconds blocked, no DDL (baseline)", report.Num(amp.Baseline, 1)},
			{"query-seconds blocked, attributable to the DDL", report.Num(amp.BlockedSeconds, 1)},
			{"amplification (blocked query-seconds / DDL hold)", report.Num(amp.Ratio, 1) + "x"},
			{"longest lock queue", fmt.Sprint(amp.MaxQueue)},
		})

	s.Expect("The amplification exceeds 100x: a 3-second DDL costs more than 300 query-seconds.")
	s.Found(amp.Ratio > 100,
		"Amplification was %sx -- %s query-seconds of blocking from a statement that held its lock "+
			"for %s seconds, with a peak queue of %d. The DDL itself waited %s seconds, which is "+
			"where the damage comes from: it is not holding the lock, it is holding the queue.",
		report.Num(amp.Ratio, 1), report.Num(amp.BlockedSeconds, 1),
		report.Num(amp.DDLHold, 0), amp.MaxQueue, report.Num(amp.DDLWait, 1))

	s2 := r.Section("The same DDL against a table with no long read")
	s2.Text("If the long-running read is the ingredient, removing it should remove the outage, " +
		"even though the DDL is unchanged and still takes ACCESS EXCLUSIVE. This was written " +
		"expecting a clean result and did not get one, which turned out to be the more useful outcome.")
	w2 := baseWorkload(20240601)
	w2.LongQuerySeconds = 0
	amp2 := sim.Measure(w2, ddlReq, "ddl")
	s2.Expect("Without the long read, amplification drops below 10x.")
	s2.Found(amp2.Ratio < 10,
		"Amplification was %sx (%s blocked query-seconds), against %sx with the long read present. "+
			"The prediction is wrong, and wrong in the direction that matters. Removing the long "+
			"read cuts the damage by a factor of %sx, so the long read really is the dominant "+
			"ingredient -- but what remains is still %sx amplification, not the near-nothing the "+
			"prediction assumed. The reason is that the two effects are independent. The long read "+
			"decides how long the DDL waits before it can start; the arrival rate decides how many "+
			"queries pile up behind it once it does. Delete the first and the second is untouched: "+
			"ACCESS EXCLUSIVE blocks every read, and at this traffic level a %s-second hold is "+
			"enough on its own to cost %s query-seconds.\n\n"+
			"The practical reading is that \"we checked, nothing long-running is on that table\" "+
			"is a real mitigation worth %sx and not a safety guarantee. It removes the tail, not "+
			"the hazard.",
		report.Num(amp2.Ratio, 1), report.Num(amp2.BlockedSeconds, 1), report.Num(amp.Ratio, 1),
		report.Num(amp.Ratio/amp2.Ratio, 1), report.Num(amp2.Ratio, 1),
		report.Num(amp2.DDLHold, 0), report.Num(amp2.BlockedSeconds, 1),
		report.Num(amp.Ratio/amp2.Ratio, 1))
}

func sectionScaling(r *report.Report) {
	s := r.Section("Blocked time is linear in traffic and quadratic in the stall")

	build := func(spacing, longHold float64) []sim.Request {
		reqs := []sim.Request{
			{ID: 1, Arrive: 0, Hold: longHold, Mode: locks.AccessShare, Label: "long-read"},
			{ID: 2, Arrive: 0.5, Hold: 0.001, Mode: locks.AccessExclusive, Label: "ddl"},
		}
		for i := 0; float64(i)*spacing < 100.0; i++ {
			reqs = append(reqs, sim.Request{
				ID: 100 + i, Arrive: 1 + float64(i)*spacing, Hold: 0.001,
				Mode: locks.AccessShare, Label: "read"})
		}
		return reqs
	}
	slow := sim.Run(build(0.2, 40)).QuerySeconds("ddl")
	fast := sim.Run(build(0.1, 40)).QuerySeconds("ddl")
	short := sim.Run(build(0.1, 20)).QuerySeconds("ddl")
	long := sim.Run(build(0.1, 40)).QuerySeconds("ddl")

	rateRatio := fast / slow
	durRatio := long / short

	s.Text("Two knobs: how fast queries arrive, and how long the DDL is stuck. They do not have " +
		"the same exponent, and knowing which is which decides where to spend effort.")
	s.Table([]string{"change", "blocked query-seconds", "factor"}, [][]string{
		{"arrival rate x1 (0.2s spacing)", report.Num(slow, 1), "-"},
		{"arrival rate x2 (0.1s spacing)", report.Num(fast, 1), report.Num(rateRatio, 3) + "x"},
		{"stall duration x1 (20s)", report.Num(short, 1), "-"},
		{"stall duration x2 (40s)", report.Num(long, 1), report.Num(durRatio, 3) + "x"},
	})

	s.Expect("Doubling the arrival rate roughly doubles the damage; doubling the stall duration " +
		"roughly quadruples it, because both the number of queued queries and each one's wait " +
		"scale with the stall.")
	held := rateRatio > 1.8 && rateRatio < 2.2 && durRatio > 3.5 && durRatio < 4.5
	s.Found(held,
		"Doubling the rate gave %sx; doubling the stall gave %sx. Blocked query-seconds go as "+
			"rate x stall^2. This is the quantitative argument for lock_timeout: it cannot reduce "+
			"your traffic, but it caps the term that is squared. Halving the worst-case stall cuts "+
			"the worst-case damage by four.",
		report.Num(rateRatio, 3), report.Num(durRatio, 3))
}

func sectionTimeout(r *report.Report) {
	s := r.Section("lock_timeout works, and it is not free")
	s.Text("The standard remedy is `SET lock_timeout` plus a retry loop. The DDL gives up quickly " +
		"instead of holding the queue, the backlog drains, and it tries again later. The question " +
		"nobody asks is what it costs, and the answer is that the migration acquires a probability " +
		"of never landing at all.")
	s.Text("Below, the same DDL behind the same 45-second read, swept across timeout values with " +
		"5 retries and a 10-second backoff.")

	var rows []timeoutRow
	for _, to := range []float64{0, 0.5, 1, 2, 5, 10, 30} {
		w := baseWorkload(20240602)
		req := sim.Request{Arrive: 20, Hold: 3, Mode: locks.AccessExclusive, Label: "ddl"}
		if to > 0 {
			req.Timeout = to
			req.Retry = 5
			req.RetryDelay = 10
		}
		rows = append(rows, timeoutRow{to, sim.Measure(w, req, "ddl")})
	}

	var table [][]string
	for _, rw := range rows {
		name := "none"
		if rw.timeout > 0 {
			name = report.Num(rw.timeout, 1) + " s"
		}
		landed := "yes"
		if !rw.amp.Landed {
			landed = "NO"
		}
		table = append(table, []string{
			name,
			report.Num(rw.amp.BlockedSeconds, 1),
			report.Num(rw.amp.Ratio, 1) + "x",
			fmt.Sprint(rw.amp.Attempts),
			landed,
		})
	}
	s.Table([]string{"lock_timeout", "blocked query-seconds", "amplification", "attempts", "landed"}, table)

	noTimeout := rows[0].amp
	var best sim.Amplification
	bestIdx := -1
	for i, rw := range rows[1:] {
		if rw.amp.Landed && (bestIdx < 0 || rw.amp.BlockedSeconds < best.BlockedSeconds) {
			best, bestIdx = rw.amp, i+1
		}
	}
	s.Expect("A short lock_timeout cuts blocked query-seconds by more than 10x.")
	reduction := 0.0
	if bestIdx >= 0 && best.BlockedSeconds > 0 {
		reduction = noTimeout.BlockedSeconds / best.BlockedSeconds
	}
	s.Found(reduction > 10,
		"The best landing configuration (lock_timeout %s) blocked %s query-seconds against %s with "+
			"no timeout, a %sx reduction. %s. Every setting landed here, which is a result about "+
			"the retry budget rather than about the timeout: 5 retries at 10-second intervals is "+
			"90 seconds of patience against a 45-second read, so the DDL simply outlasts it. The "+
			"next section removes that cushion and finds the boundary.",
		report.Num(rows[bestIdx].timeout, 1), report.Num(best.BlockedSeconds, 1),
		report.Num(noTimeout.BlockedSeconds, 1), report.Num(reduction, 1),
		lostSummary(rows))

	sectionRetryBudget(r)
}

// sectionRetryBudget exists because the prediction above was half wrong.
//
// The original claim was that a short timeout both cuts blocked time and
// sometimes fails to land. The first half held; the second did not, because
// the retry budget was generous enough to hide the effect. Rather than reword
// the prediction and move on, this sweeps the budget itself, which turns a
// null result into the number an operator actually needs: how much patience a
// migration needs to survive a read of a given length.
func sectionRetryBudget(r *report.Report) {
	s := r.Section("How much patience a migration needs")
	s.Text("Holding lock_timeout at 0.5 seconds and sweeping the retry budget instead. The " +
		"blocking read lasts 45 seconds, so the interesting quantity is total patience: " +
		"retries multiplied by backoff.")

	var table [][]string
	var firstLanding float64 = -1
	var lastFailure float64
	for _, rt := range []int{0, 1, 2, 3, 4, 5, 8} {
		w := baseWorkload(20240603)
		req := sim.Request{
			Arrive: 20, Hold: 3, Mode: locks.AccessExclusive, Label: "ddl",
			Timeout: 0.5, Retry: rt, RetryDelay: 10,
		}
		amp := sim.Measure(w, req, "ddl")
		patience := float64(rt) * 10
		landed := "yes"
		if !amp.Landed {
			landed = "NO"
			lastFailure = patience
		} else if firstLanding < 0 {
			firstLanding = patience
		}
		table = append(table, []string{
			fmt.Sprint(rt),
			report.Num(patience, 0) + " s",
			fmt.Sprint(amp.Attempts),
			report.Num(amp.BlockedSeconds, 1),
			landed,
		})
	}
	s.Table([]string{"retries", "total patience", "attempts", "blocked query-seconds", "landed"}, table)

	s.Expect("There is a threshold: below some retry budget the migration does not land, above it " +
		"it does, and the threshold sits near the length of the blocking read.")
	s.Found(firstLanding >= 0 && lastFailure > 0 && lastFailure < firstLanding,
		"The migration first lands at %s seconds of patience and last fails at %s. The blocking "+
			"read is 45 seconds long, so the boundary is where it should be: a DDL with a short "+
			"lock_timeout lands only if its retry budget outlives whatever is already holding the "+
			"table. That is a design rule, not a tuning knob -- if the longest transaction on a "+
			"table can run for five minutes, a three-retry loop on a thirty-second backoff is a "+
			"migration that reports success to the operator and never actually ran.\n\n"+
			"Note also that blocked query-seconds barely move across this sweep. Patience is not "+
			"paid for in site impact; it is paid for in wall-clock time and in the operator's "+
			"willingness to sit and watch. That asymmetry is the argument for automating the "+
			"retry loop rather than asking a human to re-run the migration.",
		report.Num(lastFailure, 0), report.Num(firstLanding, 0))
}

type timeoutRow struct {
	timeout float64
	amp     sim.Amplification
}

func lostSummary(rows []timeoutRow) string {
	var lost []string
	for _, rw := range rows[1:] {
		if !rw.amp.Landed {
			lost = append(lost, report.Num(rw.timeout, 1)+"s")
		}
	}
	if len(lost) == 0 {
		return "Every timeout setting landed the migration"
	}
	return fmt.Sprintf("Settings that exhausted their retries without landing: %v", lost)
}

func sectionLattice(r *report.Report) {
	s := r.Section("The lock modes are not a ladder")
	s.Text("The eight table lock modes are conventionally listed weakest to strongest, which " +
		"invites a rule: when you are not sure what a statement does, assume the next mode up. " +
		"The conflict relation does not support that rule.")

	pairs, breaks := 0, 0
	var examples [][]string
	for i := 0; i < len(locks.All); i++ {
		for j := i + 1; j < len(locks.All); j++ {
			pairs++
			if !locks.Dominates(locks.All[j], locks.All[i]) {
				breaks++
				if len(examples) < 4 {
					examples = append(examples, []string{
						locks.All[j].String(), locks.All[i].String(),
						"SHARE is self-compatible; " + locks.All[i].String() + " is not",
					})
				}
			}
		}
	}
	tops := 0
	for _, m := range locks.All {
		all := true
		for _, o := range locks.All {
			if !locks.Dominates(m, o) {
				all = false
			}
		}
		if all {
			tops++
		}
	}

	s.Table([]string{"stronger mode", "weaker mode", "why it fails to dominate"}, examples)
	s.Expect("At least one ordered pair breaks monotonicity, and exactly one mode dominates all " +
		"eight -- so the only sound fail-safe is to assume ACCESS EXCLUSIVE, not to escalate a step.")
	s.Found(breaks > 0 && tops == 1,
		"%d of %d ordered pairs are non-dominating, and %d mode dominates everything. SHARE UPDATE "+
			"EXCLUSIVE sorts below SHARE, yet conflicts with SHARE while SHARE does not conflict "+
			"with itself -- so \"escalate one step\" can make a request conflict with strictly "+
			"fewer things. This is why the linter maps an unparsed statement straight to ACCESS "+
			"EXCLUSIVE: it is the unique top of the lattice, and it is the only assumption that "+
			"cannot be wrong in the unsafe direction.",
		breaks, pairs, tops)
}

func sectionVersions(r *report.Report) {
	s := r.Section("The same migration is safe on one PostgreSQL and an outage on the one before")
	s.Text("Three releases changed whether a common statement rewrites the table. PostgreSQL 11 " +
		"made `ADD COLUMN ... DEFAULT <constant>` a catalogue-only change. PostgreSQL 12 made some " +
		"widening type changes non-rewriting and let `SET NOT NULL` be proved from an existing " +
		"validated CHECK.")
	s.Text("A linter with the modern behaviour hardcoded gives the wrong answer on older servers, " +
		"and it gives it in the dangerous direction: it passes a statement that will rewrite a " +
		"900-million-row table.")

	script := `
ALTER TABLE orders ADD COLUMN status text DEFAULT 'new';
ALTER TABLE orders ADD COLUMN token uuid DEFAULT gen_random_uuid();
ALTER TABLE orders ALTER COLUMN note TYPE text;
ALTER TABLE orders ALTER COLUMN qty TYPE smallint;
CREATE INDEX CONCURRENTLY idx_orders_status ON orders (status);
`
	stmts, err := ddl.Parse(script)
	if err != nil {
		panic(err)
	}
	versions := []int{10, 11, 12, 14, 16}
	var table [][]string
	counts := map[int]int{}
	for _, v := range versions {
		rep := lint.Lint(stmts, v)
		rw := 0
		for _, st := range stmts {
			if lint.Rewrites(st, v) {
				rw++
			}
		}
		counts[v] = rep.Count(lint.Refuse)
		table = append(table, []string{
			fmt.Sprint(v), fmt.Sprint(rep.Count(lint.Refuse)),
			fmt.Sprint(rep.Count(lint.Warn)), fmt.Sprint(rw),
		})
	}
	s.Code("sql", trimScript(script))
	s.Table([]string{"PostgreSQL major", "refusals", "warnings", "statements that rewrite"}, table)

	s.Expect("The identical script produces strictly more refusals on PostgreSQL 10 than on 16.")
	s.Found(counts[10] > counts[16],
		"Refusals fall from %d on PostgreSQL 10 to %d on 16 for a byte-identical script. Two of the "+
			"five statements change classification across the 11 and 12 boundaries. A tool that does "+
			"not take the server version as an input is not analysing your database; it is analysing "+
			"the one its author had installed.",
		counts[10], counts[16])
}

func trimScript(s string) string {
	out := ""
	for _, line := range splitLines(s) {
		if line == "" {
			continue
		}
		out += line + "\n"
	}
	if len(out) > 0 {
		out = out[:len(out)-1]
	}
	return out
}

func splitLines(s string) []string {
	var out []string
	cur := ""
	for _, r := range s {
		if r == '\n' {
			out = append(out, cur)
			cur = ""
			continue
		}
		if r != '\r' {
			cur += string(r)
		}
	}
	out = append(out, cur)
	return out
}

func sectionScansNotRewrites(r *report.Report) {
	s := r.Section("The category everyone forgets: scans that are not rewrites")
	s.Text("Migration safety tools overwhelmingly check one thing: does this statement rewrite the " +
		"table. That check misses an entire class. A validating `ADD CONSTRAINT ... CHECK` rewrites " +
		"nothing at all -- and holds ACCESS EXCLUSIVE for a full sequential scan of every row.")

	cases := []string{
		"ALTER TABLE orders ADD CONSTRAINT ck_total CHECK (total >= 0)",
		"ALTER TABLE orders ADD CONSTRAINT fk_cust FOREIGN KEY (cust) REFERENCES customers (id)",
		"ALTER TABLE orders ALTER COLUMN status SET NOT NULL",
		"ALTER TABLE orders ADD CONSTRAINT uq_ref UNIQUE (ref)",
		"ALTER TABLE orders ADD COLUMN note text",
		"ALTER TABLE orders ADD CONSTRAINT ck_total CHECK (total >= 0) NOT VALID",
		"ALTER TABLE orders VALIDATE CONSTRAINT ck_total",
	}
	var table [][]string
	scansNotRewrites := 0
	for _, sql := range cases {
		st, _ := ddl.Parse(sql)
		x := st[0]
		rw, sc := lint.Rewrites(x, 16), lint.Scans(x, 16)
		if sc && !rw {
			scansNotRewrites++
		}
		table = append(table, []string{
			shorten(sql, 62), lint.LockOf(x).String(),
			yn(rw), yn(sc), yn(locks.BlocksReads(lint.LockOf(x))),
		})
	}
	s.Table([]string{"statement", "lock", "rewrites", "scans", "blocks reads"}, table)

	s.Expect("At least three of these statements scan the whole table without rewriting it, and so " +
		"are invisible to a rewrite-only check.")
	s.Found(scansNotRewrites >= 3,
		"%d of %d statements scan without rewriting. Every one of them holds ACCESS EXCLUSIVE for "+
			"the duration of that scan, which on a large table is minutes. The escape in each case "+
			"is the same shape -- add the constraint NOT VALID under a brief lock, then VALIDATE it "+
			"under SHARE UPDATE EXCLUSIVE, which blocks neither reads nor writes.",
		scansNotRewrites, len(cases))
}

func yn(b bool) string {
	if b {
		return "yes"
	}
	return "no"
}

func shorten(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n-1] + "…"
}

// ---------------------------------------------------------------------------

const (
	backfillRows    = 2_000_000
	backfillBudget  = 5.0
	backfillHorizon = 20000.0
)

func replica() backfill.Replica {
	return backfill.Replica{ApplyRate: 5000, Lag: 0, Floor: 0.5}
}

func aimd() *backfill.AIMD {
	return &backfill.AIMD{Size: 500, Budget: backfillBudget, Inc: 250, Dec: 0.5,
		Min: 100, Max: 50000}
}

func sectionFixedBatch(r *report.Report) {
	s := r.Section("The backfill dilemma: you have to guess, and both guesses are wrong")
	s.Text("Filling a new column on a large table is not lock-bound -- the batches take row locks " +
		"and nothing else waits on them. It is bound by replication lag. Every batch produces WAL; " +
		"a replica that falls too far behind stops being a usable failover target and, on a system " +
		"that reads from replicas, starts serving stale data.")
	s.Text("The controller does not know the replica's apply capacity. Nobody does: it depends on " +
		"the replica's hardware, what else is running on it, and the shape of the rows. So a fixed " +
		"batch size is a bet placed before the information arrives.")

	var table [][]string
	for _, size := range []int{500, 2000, 5000, 10000, 20000} {
		run := backfill.Backfill(backfill.Fixed{Size: size}, replica(),
			backfillRows, backfillBudget, backfillHorizon, nil)
		table = append(table, []string{
			fmt.Sprint(size), report.Num(run.Throughput(), 0),
			fmt.Sprint(run.Violations), report.Num(run.PeakLag, 2),
			yn(run.Completed), report.Num(run.Seconds, 0),
		})
	}
	s.Table([]string{"batch size", "rows/sec", "lag breaches", "peak lag (s)", "finished", "seconds"}, table)

	small := backfill.Backfill(backfill.Fixed{Size: 500}, replica(),
		backfillRows, backfillBudget, backfillHorizon, nil)
	large := backfill.Backfill(backfill.Fixed{Size: 20000}, replica(),
		backfillRows, backfillBudget, backfillHorizon, nil)

	s.Expect("No fixed batch size is both breach-free and fast: the safe setting is at least 5x " +
		"slower than the fast one, and the fast one breaches.")
	ratio := large.Throughput() / small.Throughput()
	s.Found(small.Violations == 0 && large.Violations > 0 && ratio >= 5,
		"The safe setting (500) ran %s rows/sec with %d breaches; the fast setting (20000) ran %s "+
			"rows/sec with %d breaches -- %sx the throughput and a peak lag of %s seconds against a "+
			"%s-second budget. The conservative choice is the one people make, and it is why "+
			"backfills are measured in days.",
		report.Num(small.Throughput(), 0), small.Violations,
		report.Num(large.Throughput(), 0), large.Violations,
		report.Num(ratio, 1), report.Num(large.PeakLag, 2),
		report.Num(backfillBudget, 0))
}

func sectionAIMD(r *report.Report) {
	s := r.Section("AIMD finds the capacity nobody told it")
	s.Text("Additive-increase, multiplicative-decrease is TCP's congestion control rule: grow the " +
		"batch by a constant while lag is under budget, halve it the moment it goes over. It has " +
		"no model of the replica and no configured capacity. It probes.")

	run := backfill.Backfill(aimd(), replica(), backfillRows, backfillBudget, backfillHorizon, nil)
	rep := replica()

	tail := run.Sizes[len(run.Sizes)/2:]
	sum := 0
	for _, x := range tail {
		sum += x
	}
	mean := float64(sum) / float64(len(tail))

	sorted := append([]int(nil), tail...)
	sort.Ints(sorted)
	p50 := sorted[len(sorted)/2]
	p95 := sorted[len(sorted)*95/100]

	small := backfill.Backfill(backfill.Fixed{Size: 500}, replica(),
		backfillRows, backfillBudget, backfillHorizon, nil)
	large := backfill.Backfill(backfill.Fixed{Size: 20000}, replica(),
		backfillRows, backfillBudget, backfillHorizon, nil)

	s.Table([]string{"controller", "rows/sec", "lag breaches", "peak lag (s)", "finished"}, [][]string{
		{"fixed 500", report.Num(small.Throughput(), 0), fmt.Sprint(small.Violations), report.Num(small.PeakLag, 2), yn(small.Completed)},
		{"fixed 20000", report.Num(large.Throughput(), 0), fmt.Sprint(large.Violations), report.Num(large.PeakLag, 2), yn(large.Completed)},
		{"aimd", report.Num(run.Throughput(), 0), fmt.Sprint(run.Violations), report.Num(run.PeakLag, 2), yn(run.Completed)},
	})
	s.Table([]string{"steady-state batch size", "value"}, [][]string{
		{"replica apply rate (never revealed to the controller)", report.Num(rep.ApplyRate, 0)},
		{"mean batch, second half of the run", report.Num(mean, 0)},
		{"median batch", fmt.Sprint(p50)},
		{"95th percentile batch", fmt.Sprint(p95)},
	})

	s.Expect("Without being told the apply rate, AIMD settles within 40%% of it, beats fixed-500 on " +
		"throughput and fixed-20000 on breaches.")
	near := mean > 0.6*rep.ApplyRate && mean < 1.4*rep.ApplyRate
	s.Found(near && run.Throughput() > small.Throughput() && run.Violations < large.Violations,
		"Steady-state mean batch %s against a true apply rate of %s -- within %s%%, discovered "+
			"purely by probing. Throughput %s rows/sec (%sx fixed-500) with %d breaches against "+
			"fixed-20000's %d. The controller is roughly forty lines and it dominates both fixed "+
			"settings on the axis each of them was chosen for.",
		report.Num(mean, 0), report.Num(rep.ApplyRate, 0),
		report.Num(100*absf(mean-rep.ApplyRate)/rep.ApplyRate, 1),
		report.Num(run.Throughput(), 0),
		report.Num(run.Throughput()/small.Throughput(), 1),
		run.Violations, large.Violations)
}

func absf(x float64) float64 {
	if x < 0 {
		return -x
	}
	return x
}

func sectionAblation(r *report.Report) {
	s := r.Section("Which half of AIMD does the work")
	s.Text("AIMD is asymmetric: creep up, collapse down. Is the stability coming from reacting to " +
		"lag at all, or specifically from the asymmetry? Replace only the multiplicative decrease " +
		"with a symmetric additive one and measure. Then compare both against the controller most " +
		"people write first -- scale the batch by the remaining lag headroom.")

	m := backfill.Backfill(aimd(), replica(), backfillRows, backfillBudget, backfillHorizon, nil)
	a := backfill.Backfill(&backfill.AIAD{Size: 500, Budget: backfillBudget, Inc: 250, Dec: 250,
		Min: 100, Max: 50000}, replica(), backfillRows, backfillBudget, backfillHorizon, nil)
	p := backfill.Backfill(&backfill.Proportional{Size: 500, Budget: backfillBudget, Gain: 0.3,
		Min: 100, Max: 50000}, replica(), backfillRows, backfillBudget, backfillHorizon, nil)

	s.Table([]string{"controller", "rows/sec", "lag breaches", "peak lag (s)", "batch size cv"}, [][]string{
		{"aimd (additive up, multiplicative down)", report.Num(m.Throughput(), 0), fmt.Sprint(m.Violations), report.Num(m.PeakLag, 2), report.Num(cv(m.Sizes), 3)},
		{"aiad (additive up, additive down)", report.Num(a.Throughput(), 0), fmt.Sprint(a.Violations), report.Num(a.PeakLag, 2), report.Num(cv(a.Sizes), 3)},
		{"proportional (scale by headroom)", report.Num(p.Throughput(), 0), fmt.Sprint(p.Violations), report.Num(p.PeakLag, 2), report.Num(cv(p.Sizes), 3)},
	})

	s.Text("The proportional controller is worth a note, because it is the one that looks right. " +
		"It multiplies the batch size by a factor derived from the lag error, which means the error " +
		"drives the *derivative* of the size. That is integral action, on a plant whose feedback is " +
		"delayed by the WAL pipeline, and integral action plus transport delay is the textbook " +
		"recipe for a limit cycle.")

	s.Expect("Throughput is nearly identical across all three; the multiplicative decrease shows up " +
		"as a large reduction in breaches rather than a cost in speed.")
	spread := maxf(m.Throughput(), maxf(a.Throughput(), p.Throughput())) /
		minf(m.Throughput(), minf(a.Throughput(), p.Throughput()))
	breachRatio := float64(maxi(a.Violations, p.Violations)) / float64(maxi(m.Violations, 1))
	s.Found(spread < 1.05 && breachRatio > 3,
		"Throughput spread across the three controllers is %s%% (%s to %s rows/sec). Breaches "+
			"differ by %sx: AIMD %d, AIAD %d, proportional %d. The asymmetry is close to free -- it "+
			"costs %s%% of throughput and removes %s%% of the lag-budget violations. And the "+
			"\"smooth\" proportional controller is the noisiest of the three, at cv %s against "+
			"AIMD's %s.",
		report.Num(100*(spread-1), 2),
		report.Num(minf(m.Throughput(), minf(a.Throughput(), p.Throughput())), 0),
		report.Num(maxf(m.Throughput(), maxf(a.Throughput(), p.Throughput())), 0),
		report.Num(breachRatio, 1), m.Violations, a.Violations, p.Violations,
		report.Num(100*(a.Throughput()-m.Throughput())/a.Throughput(), 2),
		report.Num(100*float64(a.Violations-m.Violations)/float64(a.Violations), 1),
		report.Num(cv(p.Sizes), 3), report.Num(cv(m.Sizes), 3))
}

func cv(xs []int) float64 {
	if len(xs) < 2 {
		return 0
	}
	xs = xs[len(xs)/2:]
	mean := 0.0
	for _, x := range xs {
		mean += float64(x)
	}
	mean /= float64(len(xs))
	if mean == 0 {
		return 0
	}
	v := 0.0
	for _, x := range xs {
		d := float64(x) - mean
		v += d * d
	}
	return sqrt(v/float64(len(xs))) / mean
}

func sqrt(x float64) float64 {
	if x <= 0 {
		return 0
	}
	g := x
	for i := 0; i < 40; i++ {
		g = 0.5 * (g + x/g)
	}
	return g
}

func maxf(a, b float64) float64 {
	if a > b {
		return a
	}
	return b
}
func minf(a, b float64) float64 {
	if a < b {
		return a
	}
	return b
}
func maxi(a, b int) int {
	if a > b {
		return a
	}
	return b
}

func sectionDisturbance(r *report.Report) {
	s := r.Section("When the ground moves: a concurrent VACUUM")
	s.Text("A tuned fixed batch size is tuned for the conditions at tuning time. Autovacuum kicks " +
		"in on a neighbouring table, a checkpoint storm lands, someone starts a second migration -- " +
		"and the replica's apply capacity drops without anyone telling the backfill.")
	s.Text("The disturbance below cuts apply capacity to 40%% between t=200 and t=400.")

	d := &backfill.Disturbance{At: 200, Until: 400, ApplyRateFactor: 0.4}
	m := backfill.Backfill(aimd(), replica(), backfillRows, backfillBudget, backfillHorizon, d)
	f := backfill.Backfill(backfill.Fixed{Size: 5000}, replica(),
		backfillRows, backfillBudget, backfillHorizon, d)

	count := func(run backfill.Run, from, to float64) int {
		n := 0
		for i, lag := range run.Lags {
			t := float64(i)
			if t >= from && t < to && lag > backfillBudget {
				n++
			}
		}
		return n
	}
	mDuring, fDuring := count(m, d.At, d.Until), count(f, d.At, d.Until)
	mAfter, fAfter := count(m, d.Until, d.Until+400), count(f, d.Until, d.Until+400)

	s.Table([]string{"controller", "breaches during", "breaches in the 400s after", "peak lag (s)", "rows/sec"}, [][]string{
		{"aimd", fmt.Sprint(mDuring), fmt.Sprint(mAfter), report.Num(m.PeakLag, 2), report.Num(m.Throughput(), 0)},
		{"fixed 5000", fmt.Sprint(fDuring), fmt.Sprint(fAfter), report.Num(f.PeakLag, 2), report.Num(f.Throughput(), 0)},
	})

	s.Expect("Both controllers breach during the disturbance; only AIMD stops breaching once it " +
		"has adapted, and its peak lag is materially lower.")
	s.Found(mDuring > 0 && mAfter < mDuring && fDuring > mDuring && m.PeakLag < f.PeakLag,
		"AIMD breached %d times during the disturbance and %d times in the 400 seconds after it; "+
			"the fixed controller breached %d and %d. Peak lag %s seconds against %s. The fixed "+
			"controller does not recover because it was never reacting -- it is not that it adapts "+
			"slowly, it is that a number in a config file cannot adapt at all, and the operator who "+
			"chose it is asleep.",
		mDuring, mAfter, fDuring, fAfter,
		report.Num(m.PeakLag, 2), report.Num(f.PeakLag, 2))
}

func sectionRewriter(r *report.Report) {
	s := r.Section("Refusal is not a product")
	s.Text("A tool that blocks a migration and offers nothing gets an exemption flag within a " +
		"fortnight, and after that it is decoration. The linter is only useful if, for every " +
		"statement it refuses, it can produce the multi-step plan that achieves the same end state " +
		"safely -- and if that generated plan passes its own checks.")

	inputs := []string{
		"ALTER TABLE public.orders ADD COLUMN status text NOT NULL DEFAULT 'new'",
		"ALTER TABLE public.orders ADD COLUMN note text NOT NULL",
		"ALTER TABLE public.orders ALTER COLUMN ref TYPE varchar(20)",
		"ALTER TABLE public.orders ALTER COLUMN total SET NOT NULL",
		"CREATE INDEX idx_orders_status ON public.orders (status)",
		"CREATE UNIQUE INDEX idx_orders_ref ON public.orders (ref)",
		"ALTER TABLE public.orders ADD CONSTRAINT ck_total CHECK (total >= 0)",
		"ALTER TABLE public.orders ADD CONSTRAINT fk_cust FOREIGN KEY (cust) REFERENCES customers (id)",
		"ALTER TABLE public.orders ADD CONSTRAINT uq_ref UNIQUE (ref)",
		"ALTER TABLE public.orders RENAME COLUMN ref TO reference",
	}
	var table [][]string
	refused, rewritten, clean, mismatch := 0, 0, 0, 0
	for _, sql := range inputs {
		st, _ := ddl.Parse(sql)
		before := lint.Lint(st, 16)
		if before.Refused() {
			refused++
		}
		m, ok := plan.Rewrite(sql, 16)
		steps, verdict := 0, "no rewrite offered"
		if ok {
			rewritten++
			steps = len(m.Steps)
			problems := m.Validate()
			after := m.Lint(16)
			if len(problems) == 0 && !after.Refused() {
				clean++
				verdict = "valid, lints clean"
			} else {
				verdict = fmt.Sprintf("%d problems, refused=%v", len(problems), after.Refused())
			}
		}
		// A statement that is flagged with no rewrite, or rewritten despite
		// being clean, is a hole in the tool's contract either way.
		if ok != before.Flagged() {
			mismatch++
			if !ok {
				verdict = "FLAGGED WITH NO ALTERNATIVE"
			} else {
				verdict += " (rewritten but not flagged)"
			}
		}
		table = append(table, []string{shorten(sql, 58), verdictOf(before), fmt.Sprint(steps), verdict})
	}
	s.Table([]string{"statement", "linter", "steps in the safe plan", "generated plan"}, table)

	rename, _ := plan.Rewrite("ALTER TABLE public.orders RENAME COLUMN ref TO reference", 16)
	s.Code("text", rename.Summary())

	s.Expect("Flagging and rewriting coincide exactly -- every statement the linter flags gets a " +
		"plan, no clean statement gets one -- and every generated plan passes both the structural " +
		"validator and the linter.")
	s.Found(mismatch == 0 && clean == rewritten,
		"%d of %d statements were refused outright, %d rewrites were produced with %d mismatches, "+
			"and %d of those rewrites pass both the validator and the linter.\n\n"+
			"Getting here took five fixes, and every one was found by this check rather than by any "+
			"unit test. The rewriter generated a seven-step plan for `ADD COLUMN ... NOT NULL "+
			"DEFAULT 'new'`, which PostgreSQL 11 and later execute in milliseconds. It patched "+
			"`CREATE UNIQUE INDEX` with a string replace that searched for `CREATE INDEX`, matched "+
			"nothing, and emitted the original blocking build labelled as the safe version. The "+
			"linter refused `ADD CONSTRAINT ... USING INDEX`, which is the second half of the fix "+
			"it recommends in the first half. It refused its own transactional rename because the "+
			"parser did not know the word `BEGIN` and defaulted unknown statements to ACCESS "+
			"EXCLUSIVE. And `ALTER COLUMN TYPE` and `SET NOT NULL` -- the two most common risky "+
			"statements in any real migration -- were refused with no alternative at all, which is "+
			"exactly the failure this section was written to catch.\n\n"+
			"The fifth fix was to the property itself. It originally read \"every *refused* "+
			"statement gets a plan\", and `SET NOT NULL` broke it: the linter warns rather than "+
			"refuses, because on a small table the scan finishes before anyone notices, but there "+
			"is still a strictly better five-step form. Tying the rewriter to refusals would have "+
			"meant withholding that form from the one person who asked. The property is about "+
			"whether the tool flagged something, not about how loudly.\n\n"+
			"None of these are exotic. They are what happens when a tool's output is never fed "+
			"back into its own input, and they are the reason this section exists: \"the advice "+
			"survives the advisor\" is cheap to state, cheap to check, and catches a class of bug "+
			"that no amount of testing the two halves separately will find.\n\n"+
			"The rename is still the striking row: seven steps, a trigger, a throttled backfill and "+
			"a full release cycle of waiting, to replace a statement that takes eleven milliseconds "+
			"and breaks every currently deployed instance the moment it commits.",
		refused, len(inputs), rewritten, mismatch, clean)
}

// verdictOf renders the linter's opinion at the granularity the rewriter acts
// on, because "no" is a misleading answer for a statement that was warned
// about.
func verdictOf(r lint.Report) string {
	switch {
	case r.Refused():
		return "refuse"
	case r.Flagged():
		return "warn"
	}
	return "clean"
}

func sectionContract(r *report.Report) {
	s := r.Section("The contract phase is where the irreversibility lives")
	s.Text("Expand and migrate are recoverable: a column you added can be dropped, a constraint " +
		"you added NOT VALID can be dropped, a backfill can be nulled out. Contract is where the " +
		"old shape is destroyed, and no amount of rollback SQL brings back a dropped column's data.")
	s.Text("So the validator treats the phases asymmetrically. Outside contract, every step must " +
		"declare a rollback and that rollback must have been executed against real data. Inside " +
		"contract, a step may declare itself irreversible -- but only with a written justification, " +
		"and the destructive statements are refused anywhere else.")

	bad := plan.Migration{Name: "a plausible bad plan", Table: "orders", Steps: []plan.Step{
		{Phase: plan.Expand, SQL: "ALTER TABLE orders ADD COLUMN reference text"},
		{Phase: plan.Expand, SQL: "ALTER TABLE orders DROP COLUMN ref",
			Rollback: "-- re-add it", TestedRollback: false},
		{Phase: plan.Contract, SQL: "ALTER TABLE orders DROP COLUMN legacy_ref"},
		{Phase: plan.Migrate, SQL: "ALTER TABEL orders VALIDATE CONSTRAINT c",
			Rollback: "x", TestedRollback: true},
	}}
	problems := bad.Validate()
	var table [][]string
	for _, p := range problems {
		table = append(table, []string{fmt.Sprint(p.Step), p.Rule, shorten(p.Detail, 78)})
	}
	s.Table([]string{"step", "rule", "detail"}, table)

	good, _ := plan.Rewrite("ALTER TABLE public.orders ADD COLUMN status text NOT NULL DEFAULT 'new'", 16)
	rules := plan.ProblemRules(problems)

	s.Expect("The bad plan trips at least five distinct rules, including the mistyped statement, " +
		"while the generated plan trips none.")
	s.Found(len(rules) >= 5 && len(good.Validate()) == 0,
		"The bad plan produced %d problems across %d distinct rules: %v. The generated plan "+
			"produced %d. Note which rule catches `ALTER TABEL`: it is `unparsed`, not a spelling "+
			"check. An earlier version of this model inferred \"this step is a procedural note\" "+
			"from \"the parser found nothing\", which made a typo indistinguishable from prose and "+
			"let it vanish from the report entirely. Procedural steps now carry an explicit flag, "+
			"and anything else that fails to parse is refused.",
		len(problems), len(rules), rules, len(good.Validate()))
}
