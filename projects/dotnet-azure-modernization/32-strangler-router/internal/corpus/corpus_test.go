package corpus

import (
	"bytes"
	"encoding/json"
	"testing"
)

// docs/results.md is checked in and quoted in the README. If the generator is
// not a pure function of its seed, those numbers are fiction.
func TestGenerationIsDeterministic(t *testing.T) {
	a := Generate(200, 99, 0.2)
	b := Generate(200, 99, 0.2)
	if len(a) != len(b) {
		t.Fatalf("lengths differ: %d vs %d", len(a), len(b))
	}
	for i := range a {
		if !bytes.Equal(a[i].Legacy, b[i].Legacy) || !bytes.Equal(a[i].Modern, b[i].Modern) {
			t.Fatalf("pair %d differs between runs", i)
		}
		if a[i].Defect != b[i].Defect {
			t.Fatalf("pair %d has a different label between runs", i)
		}
	}
	if c := Generate(200, 100, 0.2); bytes.Equal(a[0].Legacy, c[0].Legacy) {
		t.Error("a different seed produced identical output")
	}
}

// Every labelled defect has to actually change the document. A defect that is
// sometimes a no-op puts an invisible ceiling on the recall of every ruleset
// and looks exactly like a diff-engine weakness. This test exists because that
// happened: CustomerTierChanged used to assign "standard" unconditionally.
func TestEveryDefectAlwaysMutates(t *testing.T) {
	for _, d := range AllDefects {
		mutated := 0
		total := 0
		// Many seeds, because the failure mode was data-dependent: it only
		// showed up when the base document already held the defect's value.
		for seed := uint64(0); seed < 300; seed++ {
			r := NewRng(seed)
			doc := base(r)
			before, _ := json.Marshal(doc)
			applyDefect(doc, d, r)
			after, _ := json.Marshal(doc)
			total++
			if !bytes.Equal(before, after) {
				mutated++
			}
		}
		if mutated != total {
			t.Errorf("%s changed the document only %d/%d times; a labelled defect "+
				"that is sometimes a no-op is a mislabelled corpus", d, mutated, total)
		}
	}
}

// Noise must never change a value anyone would call a bug: it may reorder,
// re-time and re-identify, and that is all.
func TestNoiseLeavesBusinessFieldsAlone(t *testing.T) {
	for _, p := range Generate(300, 7, 0) {
		var l, m map[string]any
		mustUnmarshal(t, p.Legacy, &l)
		mustUnmarshal(t, p.Modern, &m)
		lo := l["order"].(map[string]any)
		mo := m["order"].(map[string]any)
		for _, k := range []string{"id", "status", "currency", "total", "tax"} {
			if !jsonEqual(lo[k], mo[k]) {
				t.Fatalf("noise changed order.%s: %v != %v", k, lo[k], mo[k])
			}
		}
		if len(lo["lines"].([]any)) != len(mo["lines"].([]any)) {
			t.Fatal("noise changed the number of lines")
		}
	}
}

func TestCleanCorpusHasNoDefects(t *testing.T) {
	for i, p := range Generate(100, 3, 0) {
		if p.Defective() {
			t.Fatalf("pair %d is labelled %q in a corpus with defectRate 0", i, p.Defect)
		}
	}
}

func TestDefectRateIsApproximatelyHonoured(t *testing.T) {
	c := Generate(4000, 11, 0.2)
	n := 0
	for _, p := range c {
		if p.Defective() {
			n++
		}
	}
	if n < 700 || n > 900 {
		t.Errorf("got %d defective pairs out of 4000, want roughly 800", n)
	}
}

// Round-robin assignment matters: if defects were chosen at random, a rare
// defect could be under-sampled and the per-defect table would be noise.
func TestDefectsAreEvenlyDistributed(t *testing.T) {
	counts := map[Defect]int{}
	for _, p := range Generate(4000, 11, 0.2) {
		if p.Defective() {
			counts[p.Defect]++
		}
	}
	if len(counts) != len(AllDefects) {
		t.Fatalf("only %d of %d defects appeared", len(counts), len(AllDefects))
	}
	lo, hi := 1<<30, 0
	for _, n := range counts {
		if n < lo {
			lo = n
		}
		if n > hi {
			hi = n
		}
	}
	if hi-lo > 2 {
		t.Errorf("defect counts range from %d to %d; distribution is not round-robin", lo, hi)
	}
}

func TestGenerateEachCoversEveryDefectEqually(t *testing.T) {
	c := GenerateEach(5, 40)
	counts := map[Defect]int{}
	for _, p := range c {
		counts[p.Defect]++
	}
	for _, d := range AllDefects {
		if counts[d] != 40 {
			t.Errorf("%s appears %d times, want 40", d, counts[d])
		}
	}
	if counts[None] != 40 {
		t.Errorf("clean pairs appear %d times, want 40", counts[None])
	}
}

func TestBothSidesAreValidJSON(t *testing.T) {
	for i, p := range Generate(200, 13, 0.3) {
		var v any
		if err := json.Unmarshal(p.Legacy, &v); err != nil {
			t.Fatalf("pair %d legacy is not JSON: %v", i, err)
		}
		if err := json.Unmarshal(p.Modern, &v); err != nil {
			t.Fatalf("pair %d modern is not JSON: %v", i, err)
		}
	}
}

// The generator must not accidentally share structure between the two sides,
// or a mutation to one would show up in the other.
func TestSidesAreIndependent(t *testing.T) {
	p := Generate(1, 21, 1.0)[0]
	if bytes.Equal(p.Legacy, p.Modern) {
		t.Fatal("a defective pair should differ")
	}
	var l map[string]any
	mustUnmarshal(t, p.Legacy, &l)
	l["order"].(map[string]any)["total"] = 0.0
	var again map[string]any
	mustUnmarshal(t, p.Legacy, &again)
	if again["order"].(map[string]any)["total"] == 0.0 {
		t.Fatal("mutating the decoded document changed the stored bytes")
	}
}

func TestRngIsStable(t *testing.T) {
	r := NewRng(20240612)
	got := []uint64{r.Next(), r.Next(), r.Next()}
	r2 := NewRng(20240612)
	for i, want := range got {
		if g := r2.Next(); g != want {
			t.Fatalf("draw %d: %d != %d", i, g, want)
		}
	}
	// Below must stay in range even for awkward bounds.
	r3 := NewRng(1)
	for i := 0; i < 1000; i++ {
		if v := r3.Below(3); v < 0 || v > 2 {
			t.Fatalf("Below(3) returned %d", v)
		}
	}
}

func mustUnmarshal(t *testing.T, b []byte, v any) {
	t.Helper()
	if err := json.Unmarshal(b, v); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
}

func jsonEqual(a, b any) bool {
	x, _ := json.Marshal(a)
	y, _ := json.Marshal(b)
	return bytes.Equal(x, y)
}
