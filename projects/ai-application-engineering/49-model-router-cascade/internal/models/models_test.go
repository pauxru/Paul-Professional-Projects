package models

import (
	"math"
	"testing"

	"router/internal/calibration"
	"router/internal/workload"
)

func fleetOracle(seed uint64) (*Oracle, []Model) {
	f := Fleet()
	return NewOracle(f, seed), f
}

func TestAccuracyFallsMonotonicallyWithDifficulty(t *testing.T) {
	for _, m := range Fleet() {
		prev := 2.0
		for d := 0.0; d <= 1.0; d += 0.01 {
			a := m.Accuracy(d)
			if a > prev {
				t.Fatalf("%s accuracy rose from %.4f to %.4f at d=%.2f", m.Name, prev, a, d)
			}
			if a < 0 || a > 1 {
				t.Fatalf("%s accuracy %.4f out of range", m.Name, a)
			}
			prev = a
		}
	}
}

func TestBiggerModelsAreBetterAtEveryDifficulty(t *testing.T) {
	f := Fleet()
	for d := 0.0; d <= 1.0; d += 0.01 {
		if f[1].Accuracy(d) <= f[0].Accuracy(d) {
			t.Fatalf("mid is not better than small at d=%.2f", d)
		}
		if f[2].Accuracy(d) <= f[1].Accuracy(d) {
			t.Fatalf("large is not better than mid at d=%.2f", d)
		}
	}
}

func TestAccuracyIsHalfAtCompetence(t *testing.T) {
	for _, m := range Fleet() {
		if a := m.Accuracy(m.Competence); math.Abs(a-0.5) > 1e-9 {
			t.Errorf("%s accuracy at its own competence is %.6f, want 0.5", m.Name, a)
		}
	}
}

func TestCostIsProportionalToTokens(t *testing.T) {
	m := Fleet()[2]
	a := m.Cost(workload.Query{Tokens: 1000})
	b := m.Cost(workload.Query{Tokens: 2000})
	if math.Abs(a-m.CostPerKTok) > 1e-12 {
		t.Errorf("1k tokens cost %.6f, want %.6f", a, m.CostPerKTok)
	}
	if math.Abs(b-2*a) > 1e-12 {
		t.Errorf("2k tokens cost %.6f, want %.6f", b, 2*a)
	}
}

// Two policies that make the same call must get the same answer, or the
// comparison between them is measuring sampling noise as well as routing.
func TestOracleIsDeterministicPerQueryAndModel(t *testing.T) {
	o, _ := fleetOracle(99)
	qs := workload.Generate(500, 99)
	for _, q := range qs {
		for _, name := range []string{"small", "mid", "large"} {
			a := o.Ask(q, name)
			b := o.Ask(q, name)
			if a != b {
				t.Fatalf("query %d model %s: %+v != %+v", q.ID, name, a, b)
			}
		}
	}
}

func TestOracleAnswersDifferByModel(t *testing.T) {
	o, _ := fleetOracle(3)
	qs := workload.Generate(2000, 3)
	differ := 0
	for _, q := range qs {
		if o.Ask(q, "small").Correct != o.Ask(q, "large").Correct {
			differ++
		}
	}
	if differ < 400 {
		t.Fatalf("only %d of %d queries distinguish small from large; the fleet is "+
			"too uniform for routing to have anything to work with", differ, len(qs))
	}
}

func TestOracleAnswersDifferBySeed(t *testing.T) {
	a := NewOracle(Fleet(), 1)
	b := NewOracle(Fleet(), 2)
	qs := workload.Generate(2000, 1)
	same := 0
	for _, q := range qs {
		if a.Ask(q, "small").Correct == b.Ask(q, "small").Correct {
			same++
		}
	}
	// Two independent coin flips agree about half the time; anything near 100%
	// would mean the seed is not reaching the draw.
	if same > 1500 {
		t.Fatalf("%d of %d answers identical across seeds; seed is not being mixed in", same, len(qs))
	}
}

func TestEmpiricalAccuracyMatchesTheModelCurve(t *testing.T) {
	o, f := fleetOracle(7)
	qs := workload.Generate(60000, 7)
	// Bucket by difficulty and compare the realised rate with the curve.
	type bucket struct{ n, ok int }
	buckets := map[int]*bucket{}
	for _, q := range qs {
		b := int(q.Difficulty * 10)
		if buckets[b] == nil {
			buckets[b] = &bucket{}
		}
		buckets[b].n++
		if o.Ask(q, "mid").Correct {
			buckets[b].ok++
		}
	}
	for i, b := range buckets {
		if b.n < 500 {
			continue
		}
		mid := (float64(i) + 0.5) / 10
		want := f[1].Accuracy(mid)
		got := float64(b.ok) / float64(b.n)
		if math.Abs(got-want) > 0.06 {
			t.Errorf("difficulty bucket %.2f: realised %.3f, curve says %.3f", mid, got, want)
		}
	}
}

func TestUnknownModelPanicsRatherThanSilentlyMisreporting(t *testing.T) {
	defer func() {
		if recover() == nil {
			t.Fatal("asking an unknown model should panic, not return a zero Answer")
		}
	}()
	o, _ := fleetOracle(1)
	o.Ask(workload.Query{ID: 1, Tokens: 100}, "nonexistent")
}

func TestDistortPushesTowardsTheExtremes(t *testing.T) {
	// t > 1 is overconfidence: a belief of 0.7 is reported as something higher.
	for _, p := range []float64{0.55, 0.6, 0.7, 0.8, 0.9} {
		if got := distort(p, 1.9); got <= p {
			t.Errorf("distort(%.2f, 1.9) = %.4f, expected it to move towards 1", p, got)
		}
	}
	for _, p := range []float64{0.45, 0.4, 0.3, 0.2, 0.1} {
		if got := distort(p, 1.9); got >= p {
			t.Errorf("distort(%.2f, 1.9) = %.4f, expected it to move towards 0", p, got)
		}
	}
}

func TestDistortIsIdentityAtOne(t *testing.T) {
	for _, p := range []float64{0.01, 0.25, 0.5, 0.75, 0.99} {
		if distort(p, 1) != p {
			t.Errorf("distort(%v, 1) = %v", p, distort(p, 1))
		}
	}
}

func TestDistortFixesTheMidpoint(t *testing.T) {
	for _, temp := range []float64{0.5, 1.4, 1.9, 3.0} {
		if got := distort(0.5, temp); math.Abs(got-0.5) > 1e-12 {
			t.Errorf("distort(0.5, %v) = %v, want 0.5", temp, got)
		}
	}
}

func TestDistortIsStrictlyMonotone(t *testing.T) {
	prev := -1.0
	for p := 0.001; p < 1; p += 0.001 {
		got := distort(p, 1.9)
		if got < prev {
			t.Fatalf("distort is not monotone at p=%.3f", p)
		}
		prev = got
	}
}

// The round trip across the package boundary. distort multiplies the log-odds
// by t; calibration.Temper divides them by t. If either sign flips, the whole
// report's narrative inverts - an "overconfident" model becomes underconfident
// and the fitted temperature no longer means what section 4 says it means.
//
// I wrote this test after the sign WAS inverted and none of the single-package
// tests above noticed, because distort was monotone into (0,1) in either
// direction. Two components that implement inverse operations must be asserted
// against each other, not each against its own idea of correct.
func TestDistortAndTemperAreExactInverses(t *testing.T) {
	checked := 0
	for _, temp := range []float64{0.6, 1.1, 1.4, 1.9, 3.0} {
		for p := 0.05; p < 0.96; p += 0.05 {
			d := distort(p, temp)
			if d <= 0.001 || d >= 0.999 {
				continue // the clamp bit; see TestTheClampBreaksInvertibilityInTheTails
			}
			checked++
			if got := calibration.Temper(d, temp); math.Abs(got-p) > 1e-9 {
				t.Fatalf("Temper(distort(%.2f, %.2f), %.2f) = %.6f, want %.2f",
					p, temp, temp, got, p)
			}
		}
	}
	if checked < 60 {
		t.Fatalf("only %d unclamped pairs checked; the clamp has swallowed the test", checked)
	}
}

// distort clamps its output to [0.001, 0.999] so no model ever reports absolute
// certainty. That clamp is not invertible, so recalibration cannot undo the
// distortion in the tails - and this is the reason section 4's fitted
// temperature (1.59) undershoots the simulator's true 1.90. The clamp
// compresses the most extreme reports inward, so the signal looks less
// distorted than it was generated to be, and the fit follows the evidence.
//
// Pinned rather than fixed: a real model that reports 1.0 is a real thing, and
// clamping is what a production wrapper does.
func TestTheClampBreaksInvertibilityInTheTails(t *testing.T) {
	const temp = 3.0
	d := distort(0.05, temp)
	if d != 0.001 {
		t.Fatalf("distort(0.05, 3) = %v, expected the lower clamp at 0.001", d)
	}
	back := calibration.Temper(d, temp)
	if math.Abs(back-0.05) < 0.01 {
		t.Fatal("the clamp is no longer lossy; section 4's explanation of the " +
			"1.59-vs-1.90 gap needs revisiting")
	}
}

// The consequence that section 4 relies on: fitting a temperature to a model's
// own reported confidences recovers that model's Calibration field, up to the
// clamp loss documented above. If this fails, "fitted 1.59 against a simulator
// truth of 1.90" is meaningless.
func TestFittedTemperatureRecoversTheModelsCalibration(t *testing.T) {
	o := NewOracle(Fleet(), 99)
	qs := workload.Generate(20000, 99)
	var s []calibration.Sample
	for _, q := range qs {
		a := o.Ask(q, "small")
		s = append(s, calibration.Sample{Confidence: a.Confidence, Correct: a.Correct})
	}
	got := calibration.FitTemperature(s)
	want := o.Model("small").Calibration
	if math.Abs(got-want) > 0.45 {
		t.Errorf("fitted temperature %.3f, want the model's calibration %.2f", got, want)
	}
	if got <= 1 {
		t.Errorf("fitted %.3f: the small model must read as overconfident", got)
	}
	if got > want {
		t.Errorf("fitted %.3f exceeds the truth %.2f; the clamp should bias the "+
			"fit downwards, not upwards", got, want)
	}
}

func TestRecalibrateLeavesEverythingElseAlone(t *testing.T) {
	orig := Fleet()
	fixed := Recalibrate(orig)
	for i := range orig {
		if fixed[i].Calibration != 1.0 {
			t.Errorf("%s still has calibration %v", fixed[i].Name, fixed[i].Calibration)
		}
		if fixed[i].Competence != orig[i].Competence ||
			fixed[i].CostPerKTok != orig[i].CostPerKTok ||
			fixed[i].LatencyMs != orig[i].LatencyMs {
			t.Errorf("%s changed on an axis other than calibration", fixed[i].Name)
		}
	}
	if orig[0].Calibration == 1.0 {
		t.Fatal("Recalibrate mutated the original fleet")
	}
}

func TestConfidenceIsHigherOnAnswersThatAreRight(t *testing.T) {
	o, _ := fleetOracle(31)
	qs := workload.Generate(8000, 31)
	var right, wrong, nr, nw float64
	for _, q := range qs {
		a := o.Ask(q, "small")
		if a.Correct {
			right += a.Confidence
			nr++
		} else {
			wrong += a.Confidence
			nw++
		}
	}
	if right/nr <= wrong/nw+0.2 {
		t.Fatalf("confidence carries too little signal: %.3f when right vs %.3f when wrong",
			right/nr, wrong/nw)
	}
}

func TestLatencyIsPositiveAndCentredOnTheMedian(t *testing.T) {
	o, f := fleetOracle(17)
	qs := workload.Generate(20000, 17)
	sum := 0.0
	for _, q := range qs {
		a := o.Ask(q, "large")
		if a.LatencyMs <= 0 {
			t.Fatalf("non-positive latency %v", a.LatencyMs)
		}
		sum += a.LatencyMs
	}
	mean := sum / float64(len(qs))
	// Lognormal with sigma 0.25 has mean = median * exp(sigma^2/2).
	want := f[2].LatencyMs * math.Exp(0.25*0.25/2)
	if math.Abs(mean-want)/want > 0.05 {
		t.Errorf("mean latency %.0fms, expected about %.0fms", mean, want)
	}
}

func TestDescribeListsEveryModel(t *testing.T) {
	s := Describe(Fleet())
	for _, m := range Fleet() {
		if !contains(s, m.Name) {
			t.Errorf("Describe omits %s", m.Name)
		}
	}
}

func contains(hay, needle string) bool {
	for i := 0; i+len(needle) <= len(hay); i++ {
		if hay[i:i+len(needle)] == needle {
			return true
		}
	}
	return false
}
