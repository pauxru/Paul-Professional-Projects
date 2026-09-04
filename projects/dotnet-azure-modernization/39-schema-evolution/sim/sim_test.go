package sim

import (
	"math"
	"testing"

	"evolve/locks"
)

func find(o Outcome, id int) Result {
	for _, r := range o.Results {
		if r.ID == id {
			return r
		}
	}
	panic("no such request")
}

func TestSingleRequestGrantedImmediately(t *testing.T) {
	o := Run([]Request{{ID: 1, Arrive: 0, Hold: 5, Mode: locks.AccessExclusive}})
	r := find(o, 1)
	if r.Granted != 0 {
		t.Fatalf("granted at %v", r.Granted)
	}
	if r.Released != 5 {
		t.Fatalf("released at %v", r.Released)
	}
	if r.Wait() != 0 {
		t.Fatalf("wait %v", r.Wait())
	}
}

func TestCompatibleRequestsRunConcurrently(t *testing.T) {
	o := Run([]Request{
		{ID: 1, Arrive: 0, Hold: 10, Mode: locks.AccessShare},
		{ID: 2, Arrive: 1, Hold: 10, Mode: locks.AccessShare},
	})
	if w := find(o, 2).Wait(); w != 0 {
		t.Fatalf("two reads should not block each other, waited %v", w)
	}
}

func TestConflictingRequestWaits(t *testing.T) {
	o := Run([]Request{
		{ID: 1, Arrive: 0, Hold: 10, Mode: locks.AccessShare},
		{ID: 2, Arrive: 1, Hold: 1, Mode: locks.AccessExclusive},
	})
	if w := find(o, 2).Wait(); math.Abs(w-9) > 1e-9 {
		t.Fatalf("DDL waited %v, want 9", w)
	}
}

// This is the finding the whole project exists to demonstrate. A request that
// conflicts with nothing currently held still waits, because it arrived
// behind something that does conflict. PostgreSQL does this deliberately --
// without it, a stream of reads would starve a DDL forever -- and it is the
// mechanism that converts one blocked ALTER TABLE into a total outage.
func TestNonConflictingRequestBehindAWaiterStillWaits(t *testing.T) {
	o := Run([]Request{
		{ID: 1, Arrive: 0, Hold: 100, Mode: locks.AccessShare, Label: "long-read"},
		{ID: 2, Arrive: 1, Hold: 1, Mode: locks.AccessExclusive, Label: "ddl"},
		{ID: 3, Arrive: 2, Hold: 1, Mode: locks.AccessShare, Label: "read"},
	})

	// Request 3 is ACCESS SHARE and the only held lock is ACCESS SHARE. In
	// isolation it would be granted instantly.
	if !locks.Compatible([]locks.Mode{locks.AccessShare}, locks.AccessShare) {
		t.Fatal("premise wrong: two ACCESS SHARE locks are compatible")
	}
	w := find(o, 3).Wait()
	if w <= 0 {
		t.Fatalf("read behind the DDL waited %v; queue fairness is not modelled", w)
	}
	// It waits for the long read to finish (100) plus the DDL to hold (1).
	if math.Abs(w-99) > 1e-9 {
		t.Fatalf("read waited %v, want 99", w)
	}
}

// The shape of the damage. Written expecting "doubling the number of queued
// requests more than doubles the blocked time"; that turned out to be false,
// because doubling the count while holding the spacing fixed just extends the
// arrival window and the late arrivals wait less. Measuring 10 vs 20 requests
// at 0.1s spacing gives 285.5 vs 561.0 blocked seconds -- a factor of 1.97,
// slightly *sub*-linear.
//
// The real relationship is worth more than the guess. With arrival rate L and
// block duration T, roughly L*T requests queue and each waits on average T/2,
// so blocked query-seconds goes as L*T^2/2: linear in the rate, quadratic in
// how long the DDL is stuck. That is the quantitative case for lock_timeout,
// which does nothing about L and caps T.
func TestBlockedSecondsAreLinearInRateAndQuadraticInDuration(t *testing.T) {
	build := func(spacing, longHold float64) []Request {
		reqs := []Request{
			{ID: 1, Arrive: 0, Hold: longHold, Mode: locks.AccessShare, Label: "long-read"},
			{ID: 2, Arrive: 0.5, Hold: 0.001, Mode: locks.AccessExclusive, Label: "ddl"},
		}
		window := 100.0
		for i := 0; float64(i)*spacing < window; i++ {
			reqs = append(reqs, Request{
				ID: 100 + i, Arrive: 1 + float64(i)*spacing, Hold: 0.001,
				Mode: locks.AccessShare, Label: "read",
			})
		}
		return reqs
	}

	// Rate: same block, half the spacing => twice the arrival rate.
	slow := Run(build(0.2, 40)).QuerySeconds("ddl")
	fast := Run(build(0.1, 40)).QuerySeconds("ddl")
	rateRatio := fast / slow
	if rateRatio < 1.8 || rateRatio > 2.2 {
		t.Fatalf("doubling the arrival rate scaled blocked seconds by %.3f, want ~2", rateRatio)
	}

	// Duration: same rate, twice the block.
	short := Run(build(0.1, 20)).QuerySeconds("ddl")
	long := Run(build(0.1, 40)).QuerySeconds("ddl")
	durRatio := long / short
	if durRatio < 3.5 || durRatio > 4.5 {
		t.Fatalf("doubling the block duration scaled blocked seconds by %.3f, want ~4", durRatio)
	}
	t.Logf("rate x2 -> x%.3f blocked seconds; duration x2 -> x%.3f", rateRatio, durRatio)
}

func TestDeterminism(t *testing.T) {
	w := Workload{
		ReadsPerSec: 40, WritesPerSec: 10, ReadSeconds: 0.02, WriteSeconds: 0.05,
		LongQuerySeconds: 30, Duration: 60, Seed: 7,
	}
	ddl := Request{Arrive: 5, Hold: 3, Mode: locks.AccessExclusive, Label: "ddl"}
	first := Measure(w, ddl, "ddl")
	for i := 0; i < 20; i++ {
		got := Measure(w, ddl, "ddl")
		if got != first {
			t.Fatalf("run %d differs:\n%+v\n%+v", i, got, first)
		}
	}
}

func TestSeedChangesTheResult(t *testing.T) {
	// A determinism test passes trivially if the seed is ignored.
	base := Workload{ReadsPerSec: 40, WritesPerSec: 10, ReadSeconds: 0.02,
		WriteSeconds: 0.05, LongQuerySeconds: 30, Duration: 60, Seed: 1}
	ddl := Request{Arrive: 5, Hold: 3, Mode: locks.AccessExclusive, Label: "ddl"}
	a := Measure(base, ddl, "ddl")
	base.Seed = 2
	b := Measure(base, ddl, "ddl")
	if a == b {
		t.Fatal("changing the seed did not change the outcome")
	}
}

func TestPairedDesignRemovesBackgroundContention(t *testing.T) {
	// Writes contend with each other even with no DDL present. Measuring raw
	// blocked-seconds with the DDL in place would bill that contention to the
	// migration.
	w := Workload{ReadsPerSec: 30, WritesPerSec: 60, ReadSeconds: 0.02,
		WriteSeconds: 0.02, Duration: 60, Seed: 3}
	base := Run(w.Build(nil))
	if base.QuerySeconds("ddl") <= 0 {
		t.Skip("no background contention in this workload; the paired design is untestable here")
	}
	ddl := Request{Arrive: 5, Hold: 1, Mode: locks.AccessExclusive, Label: "ddl"}
	a := Measure(w, ddl, "ddl")
	if a.Baseline <= 0 {
		t.Fatal("baseline not measured")
	}
	raw := Run(w.Build(&ddl)).QuerySeconds("ddl")
	if a.BlockedSeconds >= raw {
		t.Fatalf("paired measurement %v did not subtract the %v baseline from %v",
			a.BlockedSeconds, a.Baseline, raw)
	}
}

func TestLockTimeoutCollapsesAmplification(t *testing.T) {
	// The remedy. The DDL gives up after a short wait, the queue behind it
	// drains, and it retries.
	reqs := func(timeout float64, retry int) []Request {
		out := []Request{
			{ID: 1, Arrive: 0, Hold: 60, Mode: locks.AccessShare, Label: "long-read"},
			{ID: 2, Arrive: 1, Hold: 1, Mode: locks.AccessExclusive,
				Label: "ddl", Timeout: timeout, Retry: retry, RetryDelay: 5},
		}
		for i := 0; i < 200; i++ {
			out = append(out, Request{ID: 100 + i, Arrive: 2 + float64(i)*0.25,
				Hold: 0.01, Mode: locks.AccessShare, Label: "read"})
		}
		return out
	}
	without := Run(reqs(0, 0)).QuerySeconds("ddl")
	with := Run(reqs(1, 100)).QuerySeconds("ddl")
	if with >= without {
		t.Fatalf("lock_timeout did not reduce blocked seconds: %v vs %v", with, without)
	}
	if with > without/10 {
		t.Fatalf("expected at least a 10x reduction, got %v -> %v", without, with)
	}
}

func TestRetryExhaustionAbandons(t *testing.T) {
	// The cost of the remedy: a DDL with a short timeout and few retries may
	// simply never land. That is a different failure mode, not an absence of
	// one, and any tool recommending lock_timeout has to own it.
	o := Run([]Request{
		{ID: 1, Arrive: 0, Hold: 1000, Mode: locks.AccessShare, Label: "long-read"},
		{ID: 2, Arrive: 1, Hold: 1, Mode: locks.AccessExclusive,
			Label: "ddl", Timeout: 1, Retry: 3, RetryDelay: 1},
	})
	r := find(o, 2)
	if !r.Abandoned {
		t.Fatal("DDL should have exhausted its retries")
	}
	if o.Landed("ddl") {
		t.Fatal("Landed() disagrees with Abandoned")
	}
	if o.Abandoned() != 1 {
		t.Fatalf("abandoned count = %d", o.Abandoned())
	}
	if r.Attempts != 4 {
		t.Fatalf("attempts = %d, want 4 (initial + 3 retries)", r.Attempts)
	}
}

func TestAbandonUnblocksTheQueue(t *testing.T) {
	// When the DDL gives up, everything queued behind it must be released.
	o := Run([]Request{
		{ID: 1, Arrive: 0, Hold: 20, Mode: locks.AccessShare, Label: "long-read"},
		{ID: 2, Arrive: 1, Hold: 1, Mode: locks.AccessExclusive,
			Label: "ddl", Timeout: 2, Retry: 0},
		{ID: 3, Arrive: 2, Hold: 1, Mode: locks.AccessShare, Label: "read"},
	})
	if !find(o, 2).Abandoned {
		t.Fatal("DDL should have been abandoned")
	}
	// The read was queued behind the DDL from t=2 and is freed at t=3 when
	// the DDL's 2s timeout expires -- long before the long read ends at 20.
	if w := find(o, 3).Wait(); w > 2 {
		t.Fatalf("read waited %v after the DDL abandoned; expected release at the timeout", w)
	}
}

func TestSuccessfulRetryStillLands(t *testing.T) {
	o := Run([]Request{
		{ID: 1, Arrive: 0, Hold: 10, Mode: locks.AccessShare, Label: "long-read"},
		{ID: 2, Arrive: 1, Hold: 1, Mode: locks.AccessExclusive,
			Label: "ddl", Timeout: 1, Retry: 50, RetryDelay: 2},
	})
	if !o.Landed("ddl") {
		t.Fatal("DDL with generous retries should land")
	}
	if find(o, 2).Attempts < 2 {
		t.Fatalf("expected more than one attempt, got %d", find(o, 2).Attempts)
	}
}

func TestStaleTimeoutIsIgnored(t *testing.T) {
	// A request granted before its deadline leaves a timeout event behind. If
	// that event were honoured it would cancel an already-running statement.
	o := Run([]Request{
		{ID: 1, Arrive: 0, Hold: 1, Mode: locks.AccessShare},
		{ID: 2, Arrive: 0.5, Hold: 5, Mode: locks.AccessExclusive,
			Label: "ddl", Timeout: 10, Retry: 0},
	})
	r := find(o, 2)
	if r.TimedOut || r.Abandoned {
		t.Fatalf("granted request was cancelled by a stale timeout: %+v", r)
	}
	if math.Abs(r.Released-6) > 1e-9 {
		t.Fatalf("released at %v, want 6", r.Released)
	}
}

func TestReleaseBeforeArrivalAtTheSameInstant(t *testing.T) {
	// Event-order tie-breaking. A lock released at t=5 must be gone before a
	// request arriving at t=5 evaluates compatibility, or the arriving
	// request queues behind a lock that no longer exists.
	o := Run([]Request{
		{ID: 1, Arrive: 0, Hold: 5, Mode: locks.AccessExclusive},
		{ID: 2, Arrive: 5, Hold: 1, Mode: locks.AccessShare},
	})
	if w := find(o, 2).Wait(); w != 0 {
		t.Fatalf("waited %v for a lock released at the same instant", w)
	}
}

func TestMaxQueue(t *testing.T) {
	reqs := []Request{
		{ID: 1, Arrive: 0, Hold: 50, Mode: locks.AccessShare, Label: "long-read"},
		{ID: 2, Arrive: 1, Hold: 1, Mode: locks.AccessExclusive, Label: "ddl"},
	}
	for i := 0; i < 25; i++ {
		reqs = append(reqs, Request{ID: 100 + i, Arrive: 2 + float64(i),
			Hold: 0.1, Mode: locks.AccessShare, Label: "read"})
	}
	o := Run(reqs)
	if o.MaxQueue() < 25 {
		t.Fatalf("max queue = %d, expected at least 25", o.MaxQueue())
	}
}

func TestDuplicateIDPanics(t *testing.T) {
	defer func() {
		if recover() == nil {
			t.Fatal("expected a panic on duplicate request ids")
		}
	}()
	Run([]Request{
		{ID: 1, Arrive: 0, Hold: 1, Mode: locks.AccessShare},
		{ID: 1, Arrive: 1, Hold: 1, Mode: locks.AccessShare},
	})
}

func TestResultsAreSortedByID(t *testing.T) {
	// The map iteration inside Run would otherwise leak nondeterminism into a
	// byte-compared report.
	o := Run([]Request{
		{ID: 9, Arrive: 0, Hold: 1, Mode: locks.AccessShare},
		{ID: 3, Arrive: 0, Hold: 1, Mode: locks.AccessShare},
		{ID: 7, Arrive: 0, Hold: 1, Mode: locks.AccessShare},
	})
	for i := 1; i < len(o.Results); i++ {
		if o.Results[i-1].ID >= o.Results[i].ID {
			t.Fatalf("results not sorted: %v", o.Results)
		}
	}
}

func TestEmptyInput(t *testing.T) {
	o := Run(nil)
	if len(o.Results) != 0 || o.Horizon != 0 || o.MaxQueue() != 0 {
		t.Fatalf("empty run produced %+v", o)
	}
	if o.Landed("ddl") {
		t.Fatal("Landed must be false when the label is absent")
	}
}

func TestWorkloadBuildIsReproducible(t *testing.T) {
	w := Workload{ReadsPerSec: 20, WritesPerSec: 5, ReadSeconds: 0.02,
		WriteSeconds: 0.05, LongQuerySeconds: 10, Duration: 30, Seed: 11}
	a := w.Build(nil)
	b := w.Build(nil)
	if len(a) != len(b) {
		t.Fatalf("lengths %d vs %d", len(a), len(b))
	}
	for i := range a {
		if a[i] != b[i] {
			t.Fatalf("request %d differs", i)
		}
	}
}

func TestWorkloadIDsAreUnique(t *testing.T) {
	w := Workload{ReadsPerSec: 50, WritesPerSec: 20, ReadSeconds: 0.02,
		WriteSeconds: 0.05, LongQuerySeconds: 10, Duration: 60, Seed: 4}
	ddl := Request{Arrive: 5, Hold: 2, Mode: locks.AccessExclusive, Label: "ddl"}
	seen := map[int]bool{}
	for _, r := range w.Build(&ddl) {
		if seen[r.ID] {
			t.Fatalf("duplicate id %d", r.ID)
		}
		seen[r.ID] = true
	}
}

func TestWorkloadArrivalsWithinDuration(t *testing.T) {
	w := Workload{ReadsPerSec: 30, ReadSeconds: 0.01, Duration: 10, Seed: 2}
	for _, r := range w.Build(nil) {
		if r.Label == "long-read" {
			continue
		}
		if r.Arrive < 0 || r.Arrive >= w.Duration {
			t.Fatalf("arrival %v outside [0, %v)", r.Arrive, w.Duration)
		}
	}
}

func TestAmplificationRatioIsGuarded(t *testing.T) {
	w := Workload{ReadsPerSec: 10, ReadSeconds: 0.01, Duration: 10, Seed: 1}
	a := Measure(w, Request{Arrive: 1, Hold: 0, Mode: locks.AccessExclusive, Label: "ddl"}, "ddl")
	if math.IsNaN(a.Ratio) || math.IsInf(a.Ratio, 0) {
		t.Fatalf("zero-hold DDL produced ratio %v", a.Ratio)
	}
}

func TestConcurrentIndexBuildDoesNotBlockReads(t *testing.T) {
	// SHARE UPDATE EXCLUSIVE against a read-only workload: the amplification
	// should be zero. If this ever becomes non-zero the model has stopped
	// respecting the conflict matrix.
	w := Workload{ReadsPerSec: 50, ReadSeconds: 0.02, LongQuerySeconds: 20,
		Duration: 60, Seed: 5}
	a := Measure(w, Request{Arrive: 5, Hold: 30,
		Mode: locks.ShareUpdateExclusive, Label: "ddl"}, "ddl")
	if a.BlockedSeconds > 1e-9 {
		t.Fatalf("CREATE INDEX CONCURRENTLY blocked %v read-seconds", a.BlockedSeconds)
	}
}
