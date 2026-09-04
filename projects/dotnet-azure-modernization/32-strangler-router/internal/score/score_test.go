package score

import (
	"testing"

	"strangler/internal/corpus"
	"strangler/internal/jsondiff"
	"strangler/internal/rulesets"
)

func evaluate(t *testing.T, n rulesets.Named, pairs []corpus.Pair) Result {
	t.Helper()
	r, err := Evaluate(n.Name, n.Rules, pairs)
	if err != nil {
		t.Fatalf("Evaluate(%s): %v", n.Name, err)
	}
	return r
}

func TestConfusionMatrixArithmetic(t *testing.T) {
	r := Result{TP: 8, FP: 2, FN: 2, TN: 88}
	if got := r.Precision(); got != 0.8 {
		t.Errorf("precision = %v, want 0.8", got)
	}
	if got := r.Recall(); got != 0.8 {
		t.Errorf("recall = %v, want 0.8", got)
	}
	if got := r.F1(); got < 0.7999 || got > 0.8001 {
		t.Errorf("F1 = %v, want 0.8", got)
	}
	if got := r.NoiseFlagged(); got < 0.0222 || got > 0.0223 {
		t.Errorf("noise flagged = %v, want ~0.0222", got)
	}
	empty := Result{}
	if empty.Precision() != 1 || empty.Recall() != 1 {
		t.Error("an empty result should not report a divide-by-zero score")
	}
}

// The headline claim of the whole project, asserted rather than described:
// the tightest ruleset is perfect on this corpus, and every relaxation of it
// loses defects without gaining anything.
func TestPreciseIsPerfectAndRelaxationsOnlyLose(t *testing.T) {
	c := corpus.Generate(4000, 20240612, 0.20)

	precise := evaluate(t, rulesets.Precise, c)
	if precise.FP != 0 {
		t.Errorf("precise flagged %d clean pairs; it is supposed to be silent", precise.FP)
	}
	if precise.FN != 0 {
		t.Errorf("precise missed %d defects: %v", precise.FN, precise.MissedDefects())
	}

	exact := evaluate(t, rulesets.Exact, c)
	if exact.FP != exact.FP+exact.TN-exact.TN || exact.TN != 0 {
		t.Errorf("exact should flag every clean pair, got TN = %d", exact.TN)
	}
	if exact.Recall() != 1.0 {
		t.Errorf("exact should catch everything, recall = %v", exact.Recall())
	}

	// Every weaker ruleset flags exactly as few clean pairs as `precise` — zero —
	// so the recall it gives up buys nothing at all.
	prev := precise
	for _, n := range []rulesets.Named{
		rulesets.ValueBlind, rulesets.SubtreeIgnored, rulesets.Tolerant, rulesets.Resigned,
	} {
		got := evaluate(t, n, c)
		if got.FP != 0 {
			t.Errorf("%s: FP = %d, want 0; the premise of the comparison is that "+
				"these rulesets are not buying a reduction in noise", n.Name, got.FP)
		}
		if got.Recall() >= prev.Recall() {
			t.Errorf("%s: recall %.3f did not fall below %s (%.3f)",
				n.Name, got.Recall(), prev.Name, prev.Recall())
		}
		prev = got
	}
	if prev.Recall() > 0.5 {
		t.Errorf("the end of the road should be badly degraded, recall = %.3f", prev.Recall())
	}
}

// Naming the specific defects each ruleset hides is the part that changes
// minds, so it is pinned.
func TestPerDefectDetectionIsPinned(t *testing.T) {
	c := corpus.GenerateEach(20240613, 40)

	want := map[string][]corpus.Defect{
		"precise": nil,
		"value-blind": {
			corpus.TimestampEmptied,
			corpus.RequestIDNotAUUID,
		},
		"subtree-ignored": {
			corpus.CustomerTierChanged,
			corpus.TimestampEmptied,
			corpus.RequestIDNotAUUID,
		},
		"resigned": {
			corpus.TotalOffByAPenny,
			corpus.StatusCaseChanged,
			corpus.DiscountFieldLost,
			corpus.CustomerTierChanged,
			corpus.TimestampEmptied,
			corpus.RequestIDNotAUUID,
		},
	}
	byName := map[string]rulesets.Named{}
	for _, n := range rulesets.All {
		byName[n.Name] = n
	}
	for name, defects := range want {
		got := evaluate(t, byName[name], c)
		missed := map[corpus.Defect]bool{}
		for _, d := range got.MissedDefects() {
			missed[d] = true
		}
		for _, d := range defects {
			if !missed[d] {
				t.Errorf("%s: expected to miss %s, but it caught it", name, d)
			}
		}
		if len(missed) != len(defects) {
			t.Errorf("%s: missed %v, expected exactly %v", name, got.MissedDefects(), defects)
		}
	}
}

// Some defects survive every relaxation. Saying so is as important as the rest:
// a weak ruleset is not useless, it is unevenly useful, and that is what makes
// it hard to notice.
func TestSomeDefectsAreCaughtByEverything(t *testing.T) {
	c := corpus.GenerateEach(20240613, 20)
	always := []corpus.Defect{
		corpus.CurrencyChanged,
		corpus.LineDropped,
		corpus.CustomerIDStringly,
		corpus.PlacedAtBecameEpoch,
	}
	for _, n := range rulesets.All {
		got := evaluate(t, n, c)
		for _, d := range always {
			if got.Missed[d] != 0 {
				t.Errorf("%s missed %s %d times", n.Name, d, got.Missed[d])
			}
		}
	}
}

func TestEveryRulesetCompiles(t *testing.T) {
	for _, n := range rulesets.All {
		if _, err := jsondiff.Compile(n.Rules); err != nil {
			t.Errorf("%s: %v", n.Name, err)
		}
		if n.Story == "" {
			t.Errorf("%s has no story; the comparison is the point", n.Name)
		}
	}
}

func TestEvaluateRejectsABadRuleset(t *testing.T) {
	_, err := Evaluate("broken", []jsondiff.Rule{{Pattern: "nope", Op: jsondiff.OpIgnore}}, nil)
	if err == nil {
		t.Fatal("expected a compile error to surface")
	}
}

func TestTablesRenderEveryRow(t *testing.T) {
	c := corpus.GenerateEach(1, 5)
	var rs []Result
	for _, n := range rulesets.All {
		rs = append(rs, evaluate(t, n, c))
	}
	tbl := Table(rs)
	for _, n := range rulesets.All {
		if !contains(tbl, n.Name) {
			t.Errorf("Table is missing %s", n.Name)
		}
	}
	grid := PerDefect(rs)
	for _, d := range corpus.AllDefects {
		if !contains(grid, string(d)) {
			t.Errorf("PerDefect is missing %s", d)
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
