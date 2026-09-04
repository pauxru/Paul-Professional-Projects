package confidence

import (
	"math"
	"strings"
	"sync"
	"testing"
)

// Reference values computed from the closed form of the Wilson score interval.
// If these drift, the promotion gate has silently changed.
func TestWilsonLowerKnownValues(t *testing.T) {
	cases := []struct {
		k, n int
		z    float64
		want float64
	}{
		{0, 0, 1.96, 0},
		{1, 1, 1.96, 0.2065},
		{10, 10, 1.96, 0.7225},
		{11, 11, 2.576, 0.6237},
		{95, 100, 1.96, 0.8880},
		{50, 100, 1.96, 0.4038},
		{0, 100, 1.96, 0.0000},
		{4000, 4000, 2.576, 0.9983},
	}
	for _, c := range cases {
		got := WilsonLower(c.k, c.n, c.z)
		if math.Abs(got-c.want) > 5e-4 {
			t.Errorf("WilsonLower(%d, %d, %g) = %.4f, want %.4f", c.k, c.n, c.z, got, c.want)
		}
	}
}

// The bound must never exceed the point estimate, and must rise as evidence
// accumulates. Those two properties are what make it usable as a gate.
func TestWilsonLowerIsMonotoneAndConservative(t *testing.T) {
	prev := -1.0
	for _, n := range []int{1, 2, 5, 10, 50, 100, 500, 1000, 5000, 20000} {
		got := WilsonLower(n, n, 2.576)
		if got > 1.0 {
			t.Fatalf("n=%d: bound %.4f exceeds 1", n, got)
		}
		if got <= prev {
			t.Fatalf("n=%d: bound %.4f did not increase from %.4f", n, got, prev)
		}
		prev = got
	}
	// With failures present the bound must sit below the observed rate.
	if got, obs := WilsonLower(90, 100, 1.96), 0.9; got >= obs {
		t.Errorf("bound %.4f should be below observed %.2f", got, obs)
	}
}

func feed(tr *Tracker, route string, matched int, failed int) {
	for i := 0; i < matched; i++ {
		tr.Record(route, true, "")
	}
	for i := 0; i < failed; i++ {
		tr.Record(route, false, "boom")
	}
}

// The headline claim: a perfect record over a handful of requests is not
// evidence, and the gate must say so.
func TestSmallPerfectSampleIsNotPromoted(t *testing.T) {
	tr := NewTracker(DefaultPolicy())
	feed(tr, "GET /refunds", 60, 0)
	if got := tr.Endpoint("GET /refunds").Stage(); got != Shadow {
		t.Fatalf("60 clean requests promoted to %s; it should still be shadowing", got)
	}
	if r := tr.Endpoint("GET /refunds").Rate(); r != 1.0 {
		t.Fatalf("observed rate should be 1.0, got %v", r)
	}
	if len(tr.Events) != 0 {
		t.Errorf("no transition should have been recorded, got %v", tr.Events)
	}
}

func TestLargePerfectSampleIsPromoted(t *testing.T) {
	tr := NewTracker(DefaultPolicy())
	feed(tr, "GET /orders", 1400, 0)
	if got := tr.Endpoint("GET /orders").Stage(); got != Canary {
		t.Fatalf("stage = %s, want canary", got)
	}
}

// Evidence must not carry across stages: cutover has to be earned separately
// from shadow, on traffic that is actually being served.
func TestEachStageRequiresItsOwnEvidence(t *testing.T) {
	tr := NewTracker(DefaultPolicy())
	feed(tr, "r", 1400, 0)
	e := tr.Endpoint("r")
	if e.Stage() != Canary {
		t.Fatalf("stage = %s, want canary", e.Stage())
	}
	if e.StageTotal() >= 1400 {
		t.Fatalf("stage counters should have reset on promotion, got %d", e.StageTotal())
	}
	// One more request must not be enough to reach cutover.
	tr.Record("r", true, "")
	if e.Stage() != Canary {
		t.Fatalf("promoted to %s on a single canary request", e.Stage())
	}
	feed(tr, "r", 1400, 0)
	if e.Stage() != Cutover {
		t.Fatalf("stage = %s, want cutover after a full canary soak", e.Stage())
	}
	if len(tr.Events) != 2 {
		t.Fatalf("want 2 transitions, got %d: %v", len(tr.Events), tr.Events)
	}
}

// A burst of failures inside the recent window must roll back even when the
// lifetime average is still excellent — which is the entire reason the window
// exists.
func TestRollbackFiresDespiteAGoodLifetimeAverage(t *testing.T) {
	tr := NewTracker(DefaultPolicy())
	feed(tr, "r", 1400, 0)
	feed(tr, "r", 1400, 0) // -> cutover
	e := tr.Endpoint("r")
	if e.Stage() != Cutover {
		t.Fatalf("setup failed, stage = %s", e.Stage())
	}
	before := e.Rate()
	feed(tr, "r", 0, 3)
	if e.Stage() != Halted {
		t.Fatalf("stage = %s, want halted", e.Stage())
	}
	after := e.Rate()
	if after < 0.998 {
		t.Fatalf("lifetime rate fell to %.4f; the test no longer proves its point", after)
	}
	if after >= before {
		t.Fatalf("three failures should have moved the rate at all: %.6f -> %.6f", before, after)
	}
	if e.LastDivergence == "" {
		t.Error("the divergence that caused the rollback should be retained")
	}
}

// Rollback is evaluated before promotion. An endpoint that crosses the sample
// floor on the same request that completes a failure burst must not be promoted.
func TestRollbackIsCheckedBeforePromotion(t *testing.T) {
	p := DefaultPolicy()
	p.MinSamples = 10
	p.PromoteAbove = 0.0 // any sample would otherwise promote
	tr := NewTracker(p)
	feed(tr, "r", 7, 0)
	feed(tr, "r", 0, 3) // total 10, hits MinSamples on the third failure
	if got := tr.Endpoint("r").Stage(); got == Canary || got == Cutover {
		t.Fatalf("promoted to %s while failing", got)
	}
}

// A halted endpoint stays halted; it does not quietly recover by ageing the
// failures out of the window.
func TestHaltedDoesNotAutoRecover(t *testing.T) {
	tr := NewTracker(DefaultPolicy())
	feed(tr, "r", 1400, 0)
	feed(tr, "r", 1400, 0)
	feed(tr, "r", 0, 3)
	if tr.Endpoint("r").Stage() != Halted {
		t.Fatal("setup failed")
	}
	feed(tr, "r", 5000, 0)
	if got := tr.Endpoint("r").Stage(); got != Halted {
		t.Fatalf("stage = %s, want halted to be sticky", got)
	}
}

// A rollback clears the recent window so the endpoint cannot be promoted on the
// strength of samples taken before the incident.
func TestTransitionClearsRecentWindow(t *testing.T) {
	tr := NewTracker(DefaultPolicy())
	feed(tr, "r", 1400, 0)
	e := tr.Endpoint("r")
	if e.StageTotal() >= e.Total() {
		t.Fatalf("stage counters not reset on promotion: %d of %d lifetime",
			e.StageTotal(), e.Total())
	}
	// Two failures immediately after promotion must not trip the rollback,
	// because the window was cleared and two is below the threshold.
	feed(tr, "r", 0, 2)
	if e.Stage() != Canary {
		t.Fatalf("stage = %s, want canary", e.Stage())
	}
	feed(tr, "r", 0, 1)
	if e.Stage() != Halted {
		t.Fatalf("stage = %s, want halted on the third failure", e.Stage())
	}
}

// Shadow never rolls back: there is nothing to roll back to, and halting a
// shadow comparison just stops you learning.
func TestShadowDoesNotHalt(t *testing.T) {
	tr := NewTracker(DefaultPolicy())
	feed(tr, "r", 0, 20)
	if got := tr.Endpoint("r").Stage(); got != Shadow {
		t.Fatalf("stage = %s, want shadow", got)
	}
}

func TestEndpointsAreIndependent(t *testing.T) {
	tr := NewTracker(DefaultPolicy())
	feed(tr, "a", 1400, 0)
	feed(tr, "b", 60, 0)
	if tr.Endpoint("a").Stage() != Canary {
		t.Error("a should have been promoted")
	}
	if tr.Endpoint("b").Stage() != Shadow {
		t.Error("b should not have been promoted")
	}
	if got := len(tr.Snapshot()); got != 2 {
		t.Errorf("snapshot has %d endpoints, want 2", got)
	}
}

func TestSnapshotIsSorted(t *testing.T) {
	tr := NewTracker(DefaultPolicy())
	for _, r := range []string{"z", "m", "a"} {
		tr.Record(r, true, "")
	}
	got := tr.Snapshot()
	for i := 1; i < len(got); i++ {
		if got[i-1].Route > got[i].Route {
			t.Fatalf("snapshot not sorted: %v then %v", got[i-1].Route, got[i].Route)
		}
	}
}

// The tracker is written from every shadow worker at once.
func TestConcurrentRecord(t *testing.T) {
	tr := NewTracker(DefaultPolicy())
	var wg sync.WaitGroup
	for w := 0; w < 8; w++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for i := 0; i < 500; i++ {
				tr.Record("r", true, "")
			}
		}()
	}
	wg.Wait()
	if got := tr.Endpoint("r").Total(); got != 4000 {
		t.Fatalf("total = %d, want 4000", got)
	}
}

func TestReportMentionsEveryEndpoint(t *testing.T) {
	tr := NewTracker(DefaultPolicy())
	feed(tr, "GET /a", 5, 0)
	feed(tr, "GET /b", 5, 0)
	rep := tr.Report()
	for _, want := range []string{"GET /a", "GET /b", "shadow", "stage wilson"} {
		if !contains(rep, want) {
			t.Errorf("report is missing %q:\n%s", want, rep)
		}
	}
}

func contains(hay, needle string) bool {
	return strings.Contains(hay, needle)
}
