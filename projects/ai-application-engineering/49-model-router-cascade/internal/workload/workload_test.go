package workload

import (
	"math"
	"testing"
)

func TestRngIsDeterministicAndSeedSensitive(t *testing.T) {
	a, b := NewRng(7), NewRng(7)
	for i := 0; i < 1000; i++ {
		if a.Next() != b.Next() {
			t.Fatalf("same seed diverged at %d", i)
		}
	}
	c, d := NewRng(7), NewRng(8)
	same := 0
	for i := 0; i < 1000; i++ {
		if c.Next() == d.Next() {
			same++
		}
	}
	if same > 2 {
		t.Fatalf("different seeds produced %d identical draws", same)
	}
}

func TestFloatStaysInUnitInterval(t *testing.T) {
	r := NewRng(99)
	for i := 0; i < 200000; i++ {
		v := r.Float()
		if v < 0 || v >= 1 {
			t.Fatalf("Float out of range: %v", v)
		}
	}
}

func TestNormalHasRoughlyTheRightMoments(t *testing.T) {
	r := NewRng(1234)
	n := 200000
	var sum, sumsq float64
	for i := 0; i < n; i++ {
		v := r.Normal()
		sum += v
		sumsq += v * v
	}
	mean := sum / float64(n)
	sd := math.Sqrt(sumsq/float64(n) - mean*mean)
	if math.Abs(mean) > 0.02 {
		t.Errorf("mean %.4f, want ~0", mean)
	}
	if math.Abs(sd-1) > 0.02 {
		t.Errorf("sd %.4f, want ~1", sd)
	}
}

func TestBelowCoversItsRange(t *testing.T) {
	r := NewRng(5)
	seen := map[int]int{}
	for i := 0; i < 30000; i++ {
		v := r.Below(7)
		if v < 0 || v >= 7 {
			t.Fatalf("Below(7) returned %d", v)
		}
		seen[v]++
	}
	if len(seen) != 7 {
		t.Fatalf("only saw %d of 7 values", len(seen))
	}
	for v, c := range seen {
		if c < 3500 || c > 5100 {
			t.Errorf("value %d appeared %d times, want roughly 4286", v, c)
		}
	}
}

func TestGenerateIsAPureFunctionOfSeed(t *testing.T) {
	a := Generate(500, 42)
	b := Generate(500, 42)
	for i := range a {
		if a[i] != b[i] {
			t.Fatalf("query %d differs between runs: %+v vs %+v", i, a[i], b[i])
		}
	}
}

func TestDifficultyStaysInRange(t *testing.T) {
	for seed := uint64(0); seed < 40; seed++ {
		for _, q := range Generate(300, seed) {
			if q.Difficulty < 0.01 || q.Difficulty > 0.99 {
				t.Fatalf("seed %d query %d difficulty %v out of range", seed, q.ID, q.Difficulty)
			}
		}
	}
}

func TestClassMixMatchesTheDeclaredShares(t *testing.T) {
	qs := Generate(60000, 3)
	counts := map[Class]int{}
	for _, q := range qs {
		counts[q.Class]++
	}
	for _, c := range Classes {
		got := float64(counts[c]) / float64(len(qs))
		want := classProfile[c].share
		if math.Abs(got-want) > 0.01 {
			t.Errorf("class %s share %.4f, want %.4f", c, got, want)
		}
	}
}

func TestHarderClassesAreActuallyHarder(t *testing.T) {
	qs := Generate(40000, 11)
	mean := map[Class]float64{}
	n := map[Class]int{}
	for _, q := range qs {
		mean[q.Class] += q.Difficulty
		n[q.Class]++
	}
	for c := range mean {
		mean[c] /= float64(n[c])
	}
	order := []Class{Lookup, Summarise, Reasoning, Code, Ambiguous}
	for i := 1; i < len(order); i++ {
		if mean[order[i]] <= mean[order[i-1]] {
			t.Errorf("%s (%.3f) is not harder than %s (%.3f)",
				order[i], mean[order[i]], order[i-1], mean[order[i-1]])
		}
	}
}

// The entire argument for cost-aware routing rests on this being small. If a
// refactor ever couples prompt size to difficulty, the section 5 result becomes
// an artefact and this test is the thing that catches it.
func TestCostAndDifficultyAreOnlyWeaklyRelated(t *testing.T) {
	qs := Generate(20000, 77)
	r := CostDifficultyCorrelation(qs)
	if r < 0 || r > 0.45 {
		t.Fatalf("log(tokens) vs difficulty correlation %.3f; the report claims cost "+
			"is largely independent of difficulty, which no longer holds", r)
	}
}

func TestPromptSizeSpansTwoOrdersOfMagnitude(t *testing.T) {
	p1, p50, p99 := TokenPercentiles(Generate(20000, 5))
	if p1 >= p50 || p50 >= p99 {
		t.Fatalf("percentiles not ordered: %d %d %d", p1, p50, p99)
	}
	if ratio := float64(p99) / float64(p1); ratio < 40 {
		t.Fatalf("p99/p1 is only %.0fx; section 5's argument needs a wide cost spread", ratio)
	}
}

// Features is the contract between the workload and every router. If a feature
// ever leaks Difficulty the whole experiment is void, so pin the vector.
func TestFeaturesNeverExposeDifficulty(t *testing.T) {
	qs := Generate(2000, 8)
	for _, q := range qs {
		for i, f := range q.Features() {
			if f == q.Difficulty {
				t.Fatalf("feature %d equals the hidden difficulty on query %d", i, q.ID)
			}
			if math.IsNaN(f) || math.IsInf(f, 0) {
				t.Fatalf("feature %d is not finite on query %d: %v", i, q.ID, f)
			}
		}
	}
}

func TestFeatureVectorHasAConstantBiasTerm(t *testing.T) {
	for _, q := range Generate(100, 2) {
		if q.Features()[0] != 1 {
			t.Fatalf("expected a bias term of 1, got %v", q.Features()[0])
		}
	}
}

func TestSplitPartitionsWithoutOverlap(t *testing.T) {
	qs := Generate(1000, 13)
	train, eval := Split(qs, 0.5)
	if len(train)+len(eval) != len(qs) {
		t.Fatalf("split lost queries: %d + %d != %d", len(train), len(eval), len(qs))
	}
	seen := map[int]bool{}
	for _, q := range train {
		seen[q.ID] = true
	}
	for _, q := range eval {
		if seen[q.ID] {
			t.Fatalf("query %d appears in both splits", q.ID)
		}
	}
}

func TestTokensAlwaysExceedContext(t *testing.T) {
	for _, q := range Generate(5000, 21) {
		if q.Tokens <= q.ContextTokens {
			t.Fatalf("query %d has %d tokens but %d of context", q.ID, q.Tokens, q.ContextTokens)
		}
		if q.Tokens <= 0 || q.Words <= 0 {
			t.Fatalf("query %d has non-positive size: %+v", q.ID, q)
		}
	}
}

func TestSummaryCoversEveryClassPresent(t *testing.T) {
	s := Summary(Generate(3000, 4))
	for _, c := range Classes {
		if !contains(s, string(c)) {
			t.Errorf("summary omits class %s", c)
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
