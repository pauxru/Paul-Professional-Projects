package frontier

import (
	"math"
	"testing"
)

func p(name string, cost, acc float64) Point {
	return Point{Name: name, CostCents: cost, Accuracy: acc}
}

func TestDominatedIsStrictOnAtLeastOneAxis(t *testing.T) {
	a := p("a", 100, 0.5)
	if a.Dominated(a) {
		t.Error("a point should not dominate itself")
	}
	if !a.Dominated(p("b", 100, 0.6)) {
		t.Error("cheaper-or-equal and strictly better should dominate")
	}
	if !a.Dominated(p("c", 90, 0.5)) {
		t.Error("strictly cheaper and equally good should dominate")
	}
	if a.Dominated(p("d", 90, 0.4)) {
		t.Error("cheaper but worse is a trade-off, not domination")
	}
}

func TestParetoDropsDominatedPoints(t *testing.T) {
	pts := []Point{
		p("cheap", 10, 0.40),
		p("dominated", 50, 0.35),
		p("mid", 50, 0.60),
		p("rich", 200, 0.80),
	}
	f := Pareto(pts)
	if len(f) != 3 {
		t.Fatalf("expected 3 non-dominated points, got %d: %+v", len(f), f)
	}
	for _, q := range f {
		if q.Name == "dominated" {
			t.Fatal("a dominated point survived")
		}
	}
	for i := 1; i < len(f); i++ {
		if f[i].CostCents < f[i-1].CostCents {
			t.Fatal("frontier is not sorted by cost")
		}
	}
}

func TestLineInterpolatesBetweenItsEndpoints(t *testing.T) {
	l := Line{Cheap: p("s", 0, 0.4), Rich: p("l", 100, 0.9)}
	for _, c := range []struct{ cost, want float64 }{
		{0, 0.4}, {50, 0.65}, {100, 0.9},
	} {
		if got := l.AccuracyAt(c.cost); math.Abs(got-c.want) > 1e-12 {
			t.Errorf("AccuracyAt(%v) = %v, want %v", c.cost, got, c.want)
		}
	}
}

func TestLineLiftIsZeroOnTheLine(t *testing.T) {
	l := Line{Cheap: p("s", 0, 0.4), Rich: p("l", 100, 0.9)}
	if got := l.Lift(p("on", 50, 0.65)); math.Abs(got) > 1e-12 {
		t.Errorf("lift %v on the line, want 0", got)
	}
	if got := l.Lift(p("above", 50, 0.70)); math.Abs(got-0.05) > 1e-12 {
		t.Errorf("lift %v, want 0.05", got)
	}
	if got := l.Lift(p("below", 50, 0.60)); math.Abs(got+0.05) > 1e-12 {
		t.Errorf("lift %v, want -0.05", got)
	}
}

func TestDegenerateLineDoesNotDivideByZero(t *testing.T) {
	l := Line{Cheap: p("a", 10, 0.5), Rich: p("b", 10, 0.5)}
	if got := l.AccuracyAt(10); got != 0.5 {
		t.Errorf("got %v", got)
	}
	if got := l.Savings(p("x", 10, 0.5)); got != 0 {
		t.Errorf("got %v", got)
	}
}

// The correction at the heart of section 2: a mid point above the chord must
// end up on the hull, so that anything measured against the hull has to beat it.
func TestHullIncludesAnAboveChordMidpoint(t *testing.T) {
	small := p("small", 45, 0.43)
	mid := p("mid", 675, 0.685)
	large := p("large", 4501, 0.875)
	h := BuildHull([]Point{small, mid, large})
	if len(h.Vertices) != 3 {
		t.Fatalf("expected all three on the hull, got %d: %+v", len(h.Vertices), h.Vertices)
	}
	chord := Line{Cheap: small, Rich: large}
	if chord.Lift(mid) <= 0 {
		t.Fatal("test fixture is wrong: mid is not above the chord")
	}
	if lift := h.Lift(mid); math.Abs(lift) > 1e-12 {
		t.Errorf("a hull vertex must have zero lift against its own hull, got %v", lift)
	}
	// And the hull must be strictly harder to beat than the chord.
	probe := p("probe", 675, 0.60)
	if h.Lift(probe) >= chord.Lift(probe) {
		t.Error("the hull should be at least as demanding as the chord everywhere")
	}
}

func TestHullExcludesAnUnderChordMidpoint(t *testing.T) {
	small := p("small", 10, 0.4)
	bad := p("bad", 100, 0.45)
	large := p("large", 200, 0.9)
	h := BuildHull([]Point{small, bad, large})
	for _, v := range h.Vertices {
		if v.Name == "bad" {
			t.Fatalf("a point below the chord ended up on the hull: %+v", h.Vertices)
		}
	}
}

func TestHullIsConcave(t *testing.T) {
	h := BuildHull([]Point{
		p("a", 10, 0.30), p("b", 40, 0.55), p("c", 100, 0.70),
		p("d", 300, 0.80), p("e", 900, 0.85),
	})
	prevSlope := math.Inf(1)
	for i := 1; i < len(h.Vertices); i++ {
		s := (h.Vertices[i].Accuracy - h.Vertices[i-1].Accuracy) /
			(h.Vertices[i].CostCents - h.Vertices[i-1].CostCents)
		if s > prevSlope+1e-12 {
			t.Fatalf("hull slope increased at vertex %d: %v then %v", i, prevSlope, s)
		}
		prevSlope = s
	}
}

func TestHullNeverSitsBelowAnyInputPoint(t *testing.T) {
	pts := []Point{
		p("a", 10, 0.30), p("b", 40, 0.55), p("c", 100, 0.70),
		p("d", 300, 0.80), p("e", 900, 0.85), p("f", 55, 0.61),
	}
	h := BuildHull(pts)
	for _, q := range pts {
		if h.AccuracyAt(q.CostCents) < q.Accuracy-1e-12 {
			t.Errorf("hull is below input point %s (%v < %v)", q.Name, h.AccuracyAt(q.CostCents), q.Accuracy)
		}
	}
}

func TestHullClampsOutsideItsRange(t *testing.T) {
	h := BuildHull([]Point{p("a", 10, 0.3), p("b", 100, 0.8)})
	if got := h.AccuracyAt(0); got != 0.3 {
		t.Errorf("below the cheapest vertex: %v, want 0.3", got)
	}
	if got := h.AccuracyAt(1e9); got != 0.8 {
		t.Errorf("above the dearest vertex: %v, want 0.8", got)
	}
	if got := h.CostAt(0.1); got != 10 {
		t.Errorf("CostAt below range: %v, want 10", got)
	}
	if got := h.CostAt(0.99); got != 100 {
		t.Errorf("CostAt above range: %v, want 100", got)
	}
}

func TestHullCostAtInvertsAccuracyAt(t *testing.T) {
	h := BuildHull([]Point{p("a", 10, 0.3), p("b", 100, 0.7), p("c", 500, 0.9)})
	for c := 10.0; c <= 500; c += 7 {
		a := h.AccuracyAt(c)
		back := h.CostAt(a)
		if math.Abs(back-c) > 1e-6 {
			t.Fatalf("CostAt(AccuracyAt(%v)) = %v", c, back)
		}
	}
}

func TestHullSavingsIsPositiveForACheaperPolicy(t *testing.T) {
	h := BuildHull([]Point{p("a", 100, 0.4), p("b", 500, 0.8)})
	// Same accuracy as the hull's midpoint, but half the price.
	mid := h.AccuracyAt(300)
	got := h.Savings(p("good", 150, mid))
	if math.Abs(got-0.5) > 1e-9 {
		t.Errorf("savings %.6f, want 0.5", got)
	}
	if bad := h.Savings(p("bad", 450, mid)); bad >= 0 {
		t.Errorf("a dearer policy at matched accuracy should show negative savings, got %v", bad)
	}
}

func TestEmptyHullIsHarmless(t *testing.T) {
	var h Hull
	if h.AccuracyAt(10) != 0 || h.CostAt(0.5) != 0 || h.Savings(p("x", 1, 1)) != 0 {
		t.Error("an empty hull should return zeroes rather than panic")
	}
}

func TestHullIgnoresDuplicateCosts(t *testing.T) {
	h := BuildHull([]Point{p("a", 10, 0.3), p("worse", 10, 0.1), p("b", 100, 0.8)})
	for _, v := range h.Vertices {
		if v.Name == "worse" {
			t.Fatal("the worse of two points at the same cost survived")
		}
	}
}

func TestTablesRenderEveryPolicy(t *testing.T) {
	pts := []Point{p("alpha", 10, 0.3), p("beta", 100, 0.8)}
	h := BuildHull(pts)
	l := Line{Cheap: pts[0], Rich: pts[1]}
	for _, s := range []string{Table(pts, l), HullTable(pts, h)} {
		for _, want := range []string{"alpha", "beta", "policy", "accuracy"} {
			if !contains(s, want) {
				t.Errorf("table omits %q:\n%s", want, s)
			}
		}
	}
}

func TestPlotMarksEveryPointAndDoesNotPanic(t *testing.T) {
	pts := []Point{p("a", 10, 0.3), p("b", 60, 0.6), p("c", 100, 0.8)}
	h := BuildHull(pts)
	s := Plot(pts, h, 40, 10)
	for i, q := range pts {
		if !contains(s, q.Name) {
			t.Errorf("plot omits %s", q.Name)
		}
		if !contains(s, string(rune('A'+i))) {
			t.Errorf("plot omits marker for %s", q.Name)
		}
	}
}

func TestPlotDegeneratesQuietly(t *testing.T) {
	pts := []Point{p("a", 10, 0.3), p("b", 10, 0.3)}
	if got := Plot(pts, BuildHull(pts), 40, 10); got != "" {
		t.Errorf("expected an empty plot for a degenerate range, got:\n%s", got)
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
