package backfill

import (
	"math"
	"testing"
)

func rep() Replica { return Replica{ApplyRate: 5000, Lag: 0, Floor: 0.5} }

func TestReplicaLagAccumulatesAndDrains(t *testing.T) {
	r := rep()
	r.Apply(10000, 1) // 2s of WAL in 1s of wall clock
	if math.Abs(r.Lag-1.0) > 1e-9 {
		t.Fatalf("lag = %v, want 1", r.Lag)
	}
	r.Apply(0, 1)
	if r.Lag != r.Floor {
		t.Fatalf("lag should drain to the floor, got %v", r.Lag)
	}
}

func TestReplicaFloor(t *testing.T) {
	r := rep()
	for i := 0; i < 100; i++ {
		r.Apply(0, 1)
	}
	if r.Lag != 0.5 {
		t.Fatalf("lag went below the floor: %v", r.Lag)
	}
}

func TestZeroApplyRateIsInert(t *testing.T) {
	r := Replica{ApplyRate: 0}
	r.Apply(1e9, 1)
	if r.Lag != 0 {
		t.Fatalf("lag = %v on a replica with no apply rate", r.Lag)
	}
}

func TestFixedSmallIsSafeAndSlow(t *testing.T) {
	run := Backfill(Fixed{Size: 500}, rep(), 4_000_000, 5, 2000, nil)
	if run.Violations != 0 {
		t.Fatalf("a batch well under capacity should never breach: %d", run.Violations)
	}
	if run.Completed {
		t.Fatal("500 rows/s cannot finish 4M rows inside a 2000s window")
	}
	if run.Rows != 1_000_000 {
		t.Fatalf("processed %d rows in 2000 batches of 500", run.Rows)
	}
}

func TestFixedLargeIsFastAndUnsafe(t *testing.T) {
	run := Backfill(Fixed{Size: 20000}, rep(), 400_000, 5, 2000, nil)
	if run.Violations == 0 {
		t.Fatal("a batch 4x the apply rate must breach the lag budget")
	}
	if !run.Completed {
		t.Fatal("it should at least finish")
	}
}

// The trade-off the controller exists to break: with a fixed batch size you
// choose between wasting the maintenance window and blowing the lag budget,
// and you have to choose in advance, without knowing the replica's capacity.
func TestFixedSizeForcesAChoice(t *testing.T) {
	small := Backfill(Fixed{Size: 500}, rep(), 400_000, 5, 5000, nil)
	large := Backfill(Fixed{Size: 20000}, rep(), 400_000, 5, 5000, nil)
	if small.Violations != 0 {
		t.Fatalf("small should be clean, got %d violations", small.Violations)
	}
	if large.Violations == 0 {
		t.Fatal("large should breach")
	}
	if large.Seconds >= small.Seconds {
		t.Fatalf("large (%v s) should be faster than small (%v s)", large.Seconds, small.Seconds)
	}
}

func newAIMD(budget float64) *AIMD {
	return &AIMD{Size: 500, Budget: budget, Inc: 250, Dec: 0.5, Min: 100, Max: 50000}
}

func TestAIMDConvergesWithoutBeingToldTheCapacity(t *testing.T) {
	// It is never given ApplyRate. It finds it.
	r := rep()
	run := Backfill(newAIMD(5), r, 2_000_000, 5, 5000, nil)
	if !run.Completed {
		t.Fatal("AIMD should finish")
	}
	tail := run.Sizes[len(run.Sizes)/2:]
	sum := 0
	for _, s := range tail {
		sum += s
	}
	mean := float64(sum) / float64(len(tail))
	// The steady-state mean batch should land near the replica's true apply
	// rate, which the controller has no way to read.
	if mean < 0.6*r.ApplyRate || mean > 1.6*r.ApplyRate {
		t.Fatalf("steady-state batch %v is nowhere near the apply rate %v", mean, r.ApplyRate)
	}
}

func TestAIMDBeatsBothFixedChoices(t *testing.T) {
	total, budget, horizon := 2_000_000, 5.0, 20000.0
	small := Backfill(Fixed{Size: 500}, rep(), total, budget, horizon, nil)
	large := Backfill(Fixed{Size: 20000}, rep(), total, budget, horizon, nil)
	aimd := Backfill(newAIMD(budget), rep(), total, budget, horizon, nil)

	if aimd.Throughput() <= small.Throughput() {
		t.Fatalf("AIMD %.0f rows/s did not beat fixed-small %.0f", aimd.Throughput(), small.Throughput())
	}
	if aimd.Violations >= large.Violations {
		t.Fatalf("AIMD had %d violations vs fixed-large %d", aimd.Violations, large.Violations)
	}
	t.Logf("small %.0f rows/s / %d breaches; large %.0f / %d; aimd %.0f / %d",
		small.Throughput(), small.Violations,
		large.Throughput(), large.Violations,
		aimd.Throughput(), aimd.Violations)
}

func TestAIMDRecoversFromADisturbance(t *testing.T) {
	// A concurrent VACUUM halves the replica's apply capacity mid-run. The
	// controller was tuned against the old capacity and has to find the new
	// one without being told anything changed.
	d := &Disturbance{At: 200, Until: 400, ApplyRateFactor: 0.4}
	run := Backfill(newAIMD(5), rep(), 2_000_000, 5, 5000, d)

	during, after := 0, 0
	for i, lag := range run.Lags {
		t := float64(i)
		if t >= d.At && t < d.Until && lag > 5 {
			during++
		}
		if t >= d.Until+100 && lag > 5 {
			after++
		}
	}
	if during == 0 {
		t.Fatal("the disturbance should have caused at least one breach")
	}
	if after > during/2 {
		t.Fatalf("did not recover: %d breaches after vs %d during", after, during)
	}
}

func TestFixedDoesNotRecoverFromADisturbance(t *testing.T) {
	// The contrast that makes the previous test mean something.
	d := &Disturbance{At: 200, Until: 400, ApplyRateFactor: 0.4}
	fixed := Backfill(Fixed{Size: 5000}, rep(), 2_000_000, 5, 5000, d)
	aimd := Backfill(newAIMD(5), rep(), 2_000_000, 5, 5000, d)

	fixedDuring, aimdDuring := 0, 0
	for i := range fixed.Lags {
		if float64(i) >= d.At && float64(i) < d.Until && fixed.Lags[i] > 5 {
			fixedDuring++
		}
	}
	for i := range aimd.Lags {
		if float64(i) >= d.At && float64(i) < d.Until && aimd.Lags[i] > 5 {
			aimdDuring++
		}
	}
	if fixedDuring <= aimdDuring {
		t.Fatalf("fixed had %d breaches during the disturbance, AIMD %d", fixedDuring, aimdDuring)
	}
	if fixed.PeakLag <= aimd.PeakLag {
		t.Fatalf("fixed peak lag %.2f, AIMD %.2f", fixed.PeakLag, aimd.PeakLag)
	}
}

func TestAIMDRespectsBounds(t *testing.T) {
	c := &AIMD{Size: 500, Budget: 5, Inc: 250, Dec: 0.5, Min: 100, Max: 1000}
	for i := 0; i < 100; i++ {
		if n := c.Next(0); n > 1000 {
			t.Fatalf("exceeded Max: %d", n)
		}
	}
	for i := 0; i < 100; i++ {
		if n := c.Next(99); n < 100 {
			t.Fatalf("fell below Min: %d", n)
		}
	}
}

// Written expecting the proportional controller to be the smooth-but-reckless
// option: low variation, high throughput, more breaches. The first half of
// that was wrong by a factor of three and a half. Proportional runs at
// coefficient of variation 0.731 against AIMD's 0.207 -- it is the *noisier*
// controller, not the smoother one.
//
// The mechanism is that it is not a proportional controller. It multiplies
// the batch size by a factor derived from the lag error, so the error drives
// the derivative of the size: integral action, on a plant whose feedback is
// delayed by the WAL pipeline. Integral action plus transport delay gives a
// limit cycle, and the cycle is large because the multiplication compounds.
//
// AIMD avoids it by being asymmetric. Additive increase means the size cannot
// compound upward, and multiplicative decrease means one breach cancels many
// increments. The next test isolates which half matters.
func TestProportionalOscillatesMoreThanAIMD(t *testing.T) {
	budget := 5.0
	p := &Proportional{Size: 500, Budget: budget, Gain: 0.3, Min: 100, Max: 50000}
	a := newAIMD(budget)
	total, horizon := 2_000_000, 20000.0

	pr := Backfill(p, rep(), total, budget, horizon, nil)
	ar := Backfill(a, rep(), total, budget, horizon, nil)

	pv := variation(pr.Sizes)
	av := variation(ar.Sizes)
	if pv <= av {
		t.Fatalf("expected proportional to be noisier: cv %.3f vs aimd %.3f", pv, av)
	}
	if pv < 2*av {
		t.Fatalf("expected the gap to be large: %.3f vs %.3f", pv, av)
	}
	if pr.Violations <= ar.Violations {
		t.Fatalf("proportional %d violations, aimd %d", pr.Violations, ar.Violations)
	}
	t.Logf("proportional: cv %.3f, %d breaches, peak lag %.2f, %.0f rows/s",
		pv, pr.Violations, pr.PeakLag, pr.Throughput())
	t.Logf("aimd:         cv %.3f, %d breaches, peak lag %.2f, %.0f rows/s",
		av, ar.Violations, ar.PeakLag, ar.Throughput())
}

// Which half of AIMD provides the stability? Replace only the multiplicative
// decrease with an additive one and measure.
func TestMultiplicativeDecreaseIsWhatDamps(t *testing.T) {
	budget := 5.0
	total, horizon := 2_000_000, 20000.0

	aimd := Backfill(newAIMD(budget), rep(), total, budget, horizon, nil)
	aiad := Backfill(&AIAD{Size: 500, Budget: budget, Inc: 250, Dec: 250,
		Min: 100, Max: 50000}, rep(), total, budget, horizon, nil)

	mv := variation(aimd.Sizes)
	av := variation(aiad.Sizes)
	t.Logf("aimd: cv %.3f, %d breaches, peak %.2f, %.0f rows/s",
		mv, aimd.Violations, aimd.PeakLag, aimd.Throughput())
	t.Logf("aiad: cv %.3f, %d breaches, peak %.2f, %.0f rows/s",
		av, aiad.Violations, aiad.PeakLag, aiad.Throughput())

	if aiad.Violations <= aimd.Violations {
		t.Fatalf("AIAD %d breaches vs AIMD %d: the asymmetry is not doing the work",
			aiad.Violations, aimd.Violations)
	}
}

func variation(xs []int) float64 {
	if len(xs) < 2 {
		return 0
	}
	// Measured over the second half, after the initial ramp.
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
	return math.Sqrt(v/float64(len(xs))) / mean
}

func TestDeterminism(t *testing.T) {
	first := Backfill(newAIMD(5), rep(), 500_000, 5, 5000, nil)
	for i := 0; i < 20; i++ {
		got := Backfill(newAIMD(5), rep(), 500_000, 5, 5000, nil)
		if got.Seconds != first.Seconds || got.Violations != first.Violations ||
			got.Rows != first.Rows || got.PeakLag != first.PeakLag {
			t.Fatalf("run %d differs: %+v vs %+v", i, got, first)
		}
	}
}

func TestNeverOverruns(t *testing.T) {
	run := Backfill(&AIMD{Size: 100000, Budget: 5, Inc: 1000, Dec: 0.5,
		Min: 1000, Max: 1000000}, rep(), 1234, 5, 100, nil)
	if run.Rows != 1234 {
		t.Fatalf("processed %d rows, want exactly 1234", run.Rows)
	}
	total := 0
	for _, s := range run.Sizes {
		total += s
	}
	if total != 1234 {
		t.Fatalf("batch sizes sum to %d", total)
	}
}

func TestHorizonStopsTheRun(t *testing.T) {
	run := Backfill(Fixed{Size: 10}, rep(), 1_000_000, 5, 50, nil)
	if run.Completed {
		t.Fatal("should not have completed")
	}
	if run.Seconds > 50 {
		t.Fatalf("ran for %v past a 50s horizon", run.Seconds)
	}
	if run.Batches != 50 {
		t.Fatalf("batches = %d", run.Batches)
	}
}

func TestThroughputGuard(t *testing.T) {
	if (Run{}).Throughput() != 0 {
		t.Fatal("throughput of a zero run must not divide by zero")
	}
}

func TestBatchSizeFloor(t *testing.T) {
	// A controller that returns zero or negative must not stall the loop
	// forever.
	run := Backfill(Fixed{Size: 0}, rep(), 100, 5, 200, nil)
	if !run.Completed {
		t.Fatal("a zero-size controller should still make progress at 1 row per batch")
	}
	if run.Seconds != 100 {
		t.Fatalf("took %v seconds for 100 rows at the floor", run.Seconds)
	}
}

func TestControllerNames(t *testing.T) {
	names := map[string]bool{}
	for _, c := range []Controller{Fixed{Size: 1}, newAIMD(5),
		&AIAD{Size: 1, Budget: 5, Inc: 1, Dec: 1, Min: 1, Max: 2},
		&Proportional{Size: 1, Budget: 5, Gain: 0.1, Min: 1, Max: 2}} {
		n := c.Name()
		if n == "" || names[n] {
			t.Fatalf("bad or duplicate controller name %q", n)
		}
		names[n] = true
	}
}
