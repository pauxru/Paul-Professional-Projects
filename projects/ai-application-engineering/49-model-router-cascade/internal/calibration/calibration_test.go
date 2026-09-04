package calibration

import (
	"math"
	"testing"
)

// A perfectly calibrated signal must score zero ECE. Hand-built so the expected
// values are arithmetic, not "whatever the code printed".
func TestPerfectCalibrationScoresZero(t *testing.T) {
	var s []Sample
	// 100 samples at 0.9 of which exactly 90 are correct, and so on.
	for _, c := range []struct {
		conf float64
		ok   int
	}{{0.9, 90}, {0.5, 50}, {0.1, 10}} {
		for i := 0; i < 100; i++ {
			s = append(s, Sample{Confidence: c.conf, Correct: i < c.ok})
		}
	}
	r := Measure(s, 10)
	if r.ECE > 1e-9 {
		t.Errorf("ECE %.12f, want 0", r.ECE)
	}
	if r.MCE > 1e-9 {
		t.Errorf("MCE %.12f, want 0", r.MCE)
	}
	if math.Abs(r.Accuracy-0.5) > 1e-9 {
		t.Errorf("accuracy %.6f, want 0.5", r.Accuracy)
	}
}

// One bin, claimed 1.0, right half the time: ECE and MCE are both exactly 0.5.
func TestKnownECEAndMCE(t *testing.T) {
	var s []Sample
	for i := 0; i < 100; i++ {
		s = append(s, Sample{Confidence: 1.0, Correct: i < 50})
	}
	r := Measure(s, 10)
	if math.Abs(r.ECE-0.5) > 1e-9 {
		t.Errorf("ECE %.9f, want 0.5", r.ECE)
	}
	if math.Abs(r.MCE-0.5) > 1e-9 {
		t.Errorf("MCE %.9f, want 0.5", r.MCE)
	}
	// Brier: half the samples cost (1-1)^2 = 0, half cost (1-0)^2 = 1.
	if math.Abs(r.Brier-0.5) > 1e-9 {
		t.Errorf("Brier %.9f, want 0.5", r.Brier)
	}
}

func TestBrierOfAConstantHalfIsAQuarter(t *testing.T) {
	var s []Sample
	for i := 0; i < 1000; i++ {
		s = append(s, Sample{Confidence: 0.5, Correct: i%2 == 0})
	}
	if r := Measure(s, 10); math.Abs(r.Brier-0.25) > 1e-9 {
		t.Errorf("Brier %.9f, want 0.25", r.Brier)
	}
}

func TestPerfectRankingScoresAUCOne(t *testing.T) {
	s := []Sample{
		{0.1, false}, {0.2, false}, {0.3, false},
		{0.7, true}, {0.8, true}, {0.9, true},
	}
	if got := Measure(s, 10).AUC; math.Abs(got-1) > 1e-12 {
		t.Errorf("AUC %.9f, want 1", got)
	}
}

func TestInvertedRankingScoresAUCZero(t *testing.T) {
	s := []Sample{
		{0.9, false}, {0.8, false}, {0.7, false},
		{0.3, true}, {0.2, true}, {0.1, true},
	}
	if got := Measure(s, 10).AUC; math.Abs(got) > 1e-12 {
		t.Errorf("AUC %.9f, want 0", got)
	}
}

// A saturated signal produces heavy ties, and a tie-blind AUC silently reports
// 1.0 for a useless signal. Averaging ranks over ties is the fix.
func TestAllTiesScoreAUCOneHalf(t *testing.T) {
	var s []Sample
	for i := 0; i < 100; i++ {
		s = append(s, Sample{Confidence: 0.6, Correct: i%2 == 0})
	}
	if got := Measure(s, 10).AUC; math.Abs(got-0.5) > 1e-12 {
		t.Errorf("AUC %.9f, want 0.5 for a signal that ranks nothing", got)
	}
}

func TestAUCIsHalfWhenOneClassIsAbsent(t *testing.T) {
	s := []Sample{{0.9, true}, {0.8, true}}
	if got := Measure(s, 10).AUC; got != 0.5 {
		t.Errorf("AUC %v with no negatives, want the 0.5 fallback", got)
	}
}

func TestEmptyInputDoesNotPanic(t *testing.T) {
	r := Measure(nil, 10)
	if r.N != 0 || r.ECE != 0 {
		t.Errorf("unexpected report for empty input: %+v", r)
	}
}

func TestBinsPartitionEverySample(t *testing.T) {
	var s []Sample
	for i := 0; i <= 1000; i++ {
		s = append(s, Sample{Confidence: float64(i) / 1000, Correct: i%3 == 0})
	}
	r := Measure(s, 12)
	total := 0
	for _, b := range r.Bins {
		total += b.N
	}
	if total != len(s) {
		t.Fatalf("bins hold %d samples but %d were measured", total, len(s))
	}
}

func TestConfidenceOfExactlyOneLandsInTheTopBin(t *testing.T) {
	r := Measure([]Sample{{1.0, true}}, 10)
	if r.Bins[9].N != 1 {
		t.Fatalf("confidence 1.0 was not placed in the last bin: %+v", r.Bins)
	}
}

func TestTemperIsIdentityAtOne(t *testing.T) {
	for _, p := range []float64{0.01, 0.3, 0.5, 0.77, 0.99} {
		if Temper(p, 1) != p {
			t.Errorf("Temper(%v, 1) = %v", p, Temper(p, 1))
		}
	}
}

func TestTemperIsStrictlyMonotone(t *testing.T) {
	for _, temp := range []float64{0.4, 1.6, 2.5} {
		prev := -1.0
		for p := 0.001; p < 1; p += 0.001 {
			got := Temper(p, temp)
			if got < prev {
				t.Fatalf("Temper(., %v) is not monotone at p=%.3f", temp, p)
			}
			prev = got
		}
	}
}

// The identity the whole of section 4 rests on: Temper(., 1/t) undoes
// Temper(., t), because tempering divides the log-odds.
func TestTemperInvertsItself(t *testing.T) {
	for _, temp := range []float64{0.5, 1.3, 1.9, 4.0} {
		for p := 0.01; p < 1; p += 0.01 {
			back := Temper(Temper(p, temp), 1/temp)
			if math.Abs(back-p) > 1e-9 {
				t.Fatalf("Temper(Temper(%.2f, %v), 1/%v) = %.9f", p, temp, temp, back)
			}
		}
	}
}

func TestTemperMovesTowardsAHalfAboveOne(t *testing.T) {
	if got := Temper(0.95, 2.0); got >= 0.95 || got <= 0.5 {
		t.Errorf("Temper(0.95, 2) = %v, want it between 0.5 and 0.95", got)
	}
	if got := Temper(0.05, 2.0); got <= 0.05 || got >= 0.5 {
		t.Errorf("Temper(0.05, 2) = %v, want it between 0.05 and 0.5", got)
	}
}

// Fitting on a signal that is already honest must not make it worse.
func TestFitTemperatureLeavesAnHonestSignalAlone(t *testing.T) {
	var s []Sample
	for i := 0; i < 20; i++ {
		conf := 0.025 + float64(i)*0.05
		n := 400
		ok := int(conf * float64(n))
		for j := 0; j < n; j++ {
			s = append(s, Sample{Confidence: conf, Correct: j < ok})
		}
	}
	temp := FitTemperature(s)
	if math.Abs(temp-1) > 0.15 {
		t.Errorf("fitted temperature %.3f on an already-calibrated signal, want ~1", temp)
	}
}

// And on a distorted one it must recover the distortion.
func TestFitTemperatureRecoversAKnownDistortion(t *testing.T) {
	const trueTemp = 1.8
	var s []Sample
	for i := 0; i < 40; i++ {
		honest := 0.0125 + float64(i)*0.025
		// Report the honest belief with its log-odds multiplied by trueTemp,
		// which is exactly what an overconfident model does.
		reported := Temper(honest, 1/trueTemp)
		n := 600
		ok := int(honest * float64(n))
		for j := 0; j < n; j++ {
			s = append(s, Sample{Confidence: reported, Correct: j < ok})
		}
	}
	got := FitTemperature(s)
	if math.Abs(got-trueTemp) > 0.2 {
		t.Errorf("fitted temperature %.3f, want %.2f", got, trueTemp)
	}
}

func TestFitTemperatureImprovesECE(t *testing.T) {
	var s []Sample
	for i := 0; i < 40; i++ {
		honest := 0.0125 + float64(i)*0.025
		n := 400
		ok := int(honest * float64(n))
		for j := 0; j < n; j++ {
			s = append(s, Sample{Confidence: Temper(honest, 1/2.2), Correct: j < ok})
		}
	}
	before := Measure(s, 12).ECE
	temp := FitTemperature(s)
	scaled := make([]Sample, len(s))
	for i, x := range s {
		scaled[i] = Sample{Confidence: Temper(x.Confidence, temp), Correct: x.Correct}
	}
	after := Measure(scaled, 12).ECE
	if after >= before {
		t.Fatalf("ECE did not improve: %.4f -> %.4f", before, after)
	}
}

// The load-bearing negative result, stated as a property of the metric:
// a monotone rescaling cannot change the ranking, so AUC is invariant.
func TestAUCIsInvariantUnderTemperatureScaling(t *testing.T) {
	var s []Sample
	for i := 0; i < 2000; i++ {
		conf := float64(i%997) / 997
		s = append(s, Sample{Confidence: 0.001 + 0.998*conf, Correct: i%3 != 0})
	}
	base := Measure(s, 10).AUC
	for _, temp := range []float64{0.5, 1.4, 2.7} {
		scaled := make([]Sample, len(s))
		for i, x := range s {
			scaled[i] = Sample{Confidence: Temper(x.Confidence, temp), Correct: x.Correct}
		}
		if got := Measure(scaled, 10).AUC; math.Abs(got-base) > 1e-9 {
			t.Errorf("AUC changed under temperature %v: %.9f -> %.9f", temp, base, got)
		}
	}
}

func TestDiagramReportsTheHeadlineNumbers(t *testing.T) {
	s := []Sample{{0.9, true}, {0.9, false}, {0.1, false}, {0.1, true}}
	d := Measure(s, 10).Diagram()
	for _, want := range []string{"ECE", "MCE", "Brier", "AUC", "confidence"} {
		if !contains(d, want) {
			t.Errorf("diagram omits %q", want)
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
