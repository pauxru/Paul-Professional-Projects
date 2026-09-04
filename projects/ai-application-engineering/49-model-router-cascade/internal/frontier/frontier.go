// Package frontier computes the cost/quality Pareto frontier and, more
// usefully, how far a policy sits above the random-routing line.
//
// "We route 30% to the big model and get 82% accuracy at 40% of the cost" is
// not a result. The result is how that compares to escalating a *random* 30%,
// because that is what the same spend buys with no intelligence at all.
package frontier

import (
	"fmt"
	"math"
	"sort"
)

// Point is one operating point.
type Point struct {
	Name      string
	CostCents float64
	Accuracy  float64
	P95Ms     float64
	// EscalRate is the fraction of traffic sent to the expensive model. It is
	// carried here only so the report can quote it next to the cost.
	EscalRate float64
}

// Dominated reports whether p is beaten on both axes by q.
func (p Point) Dominated(q Point) bool {
	return q.CostCents <= p.CostCents && q.Accuracy >= p.Accuracy &&
		(q.CostCents < p.CostCents || q.Accuracy > p.Accuracy)
}

// Pareto returns the non-dominated points, cheapest first.
func Pareto(points []Point) []Point {
	var out []Point
	for _, p := range points {
		dominated := false
		for _, q := range points {
			if p.Dominated(q) {
				dominated = true
				break
			}
		}
		if !dominated {
			out = append(out, p)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].CostCents < out[j].CostCents })
	return out
}

// Line is the straight line between two endpoints in cost/accuracy space —
// what random escalation achieves at any budget.
type Line struct{ Cheap, Rich Point }

// AccuracyAt is the accuracy random routing buys at a given spend.
func (l Line) AccuracyAt(cost float64) float64 {
	if l.Rich.CostCents == l.Cheap.CostCents {
		return l.Cheap.Accuracy
	}
	f := (cost - l.Cheap.CostCents) / (l.Rich.CostCents - l.Cheap.CostCents)
	return l.Cheap.Accuracy + f*(l.Rich.Accuracy-l.Cheap.Accuracy)
}

// Lift is how many accuracy points a policy adds over random routing at the
// same spend. Negative means the policy is worse than a coin flip and the
// sophistication is costing money.
func (l Line) Lift(p Point) float64 { return p.Accuracy - l.AccuracyAt(p.CostCents) }

// Savings is how much cheaper a policy is than random routing at the same
// accuracy, as a fraction of the gap between the endpoints.
func (l Line) Savings(p Point) float64 {
	if l.Rich.Accuracy == l.Cheap.Accuracy {
		return 0
	}
	f := (p.Accuracy - l.Cheap.Accuracy) / (l.Rich.Accuracy - l.Cheap.Accuracy)
	randomCost := l.Cheap.CostCents + f*(l.Rich.CostCents-l.Cheap.CostCents)
	if randomCost <= 0 {
		return 0
	}
	return (randomCost - p.CostCents) / randomCost
}

// Hull is the zero-information baseline: the upper convex hull of the fixed
// policies, which random mixing between them attains at every budget in range.
//
// This is the baseline a router actually has to beat, and using the chord
// between the cheapest and dearest model instead is the most common way to
// report a routing win that is not one. If a mid-tier model sits above that
// chord — ours does, by 19 accuracy points — then "we beat random escalation to
// the frontier model" is compatible with "we are worse than just using the mid
// model", and a team can ship a router that loses money while celebrating.
type Hull struct{ Vertices []Point }

// BuildHull returns the upper convex hull of the given fixed policies.
func BuildHull(fixed []Point) Hull {
	pts := append([]Point(nil), fixed...)
	sort.Slice(pts, func(i, j int) bool {
		if pts[i].CostCents == pts[j].CostCents {
			return pts[i].Accuracy > pts[j].Accuracy
		}
		return pts[i].CostCents < pts[j].CostCents
	})
	// Monotone chain, keeping only right turns so the result is the upper hull.
	var h []Point
	for _, p := range pts {
		if len(h) > 0 && h[len(h)-1].CostCents == p.CostCents {
			continue // keep the most accurate point at each cost
		}
		for len(h) >= 2 && cross(h[len(h)-2], h[len(h)-1], p) >= 0 {
			h = h[:len(h)-1]
		}
		h = append(h, p)
	}
	return Hull{Vertices: h}
}

func cross(a, b, c Point) float64 {
	return (b.CostCents-a.CostCents)*(c.Accuracy-a.Accuracy) -
		(b.Accuracy-a.Accuracy)*(c.CostCents-a.CostCents)
}

// AccuracyAt is the best accuracy attainable at a given spend with no routing
// intelligence at all — just a randomised mix of fixed policies.
func (h Hull) AccuracyAt(cost float64) float64 {
	v := h.Vertices
	if len(v) == 0 {
		return 0
	}
	if cost <= v[0].CostCents {
		return v[0].Accuracy
	}
	if cost >= v[len(v)-1].CostCents {
		return v[len(v)-1].Accuracy
	}
	for i := 0; i+1 < len(v); i++ {
		if cost <= v[i+1].CostCents {
			f := (cost - v[i].CostCents) / (v[i+1].CostCents - v[i].CostCents)
			return v[i].Accuracy + f*(v[i+1].Accuracy-v[i].Accuracy)
		}
	}
	return v[len(v)-1].Accuracy
}

// Lift is accuracy points above the zero-information baseline at matched spend.
func (h Hull) Lift(p Point) float64 { return p.Accuracy - h.AccuracyAt(p.CostCents) }

// CostAt is the cheapest spend at which the hull reaches a given accuracy.
func (h Hull) CostAt(acc float64) float64 {
	v := h.Vertices
	if len(v) == 0 {
		return 0
	}
	if acc <= v[0].Accuracy {
		return v[0].CostCents
	}
	if acc >= v[len(v)-1].Accuracy {
		return v[len(v)-1].CostCents
	}
	for i := 0; i+1 < len(v); i++ {
		if acc <= v[i+1].Accuracy {
			f := (acc - v[i].Accuracy) / (v[i+1].Accuracy - v[i].Accuracy)
			return v[i].CostCents + f*(v[i+1].CostCents-v[i].CostCents)
		}
	}
	return v[len(v)-1].CostCents
}

// Savings is the fraction of spend a policy saves against the hull at matched
// accuracy. This is the number a finance conversation wants.
func (h Hull) Savings(p Point) float64 {
	c := h.CostAt(p.Accuracy)
	if c <= 0 {
		return 0
	}
	return (c - p.CostCents) / c
}

// HullTable renders points against the zero-information baseline.
func HullTable(points []Point, h Hull) string {
	s := fmt.Sprintf("%-24s %11s %10s %10s %13s %12s\n",
		"policy", "cost", "accuracy", "escalated", "vs baseline", "cost saved")
	s += repeat("-", 86) + "\n"
	for _, p := range points {
		lift := h.Lift(p)
		verdict := "on the line"
		if lift > 0.002 || lift < -0.002 {
			verdict = fmt.Sprintf("%+.1f pts", lift*100)
		}
		save := h.Savings(p)
		saveStr := "-"
		if math.Abs(save) > 0.005 {
			saveStr = fmt.Sprintf("%+.0f%%", save*100)
		}
		s += fmt.Sprintf("%-24s %10.0f¢ %9.1f%% %9.0f%% %13s %12s\n",
			p.Name, p.CostCents, p.Accuracy*100, p.EscalRate*100, verdict, saveStr)
	}
	return s
}

// Table renders points against the random-routing line.
func Table(points []Point, l Line) string {
	s := fmt.Sprintf("%-22s %11s %10s %11s %12s %11s\n",
		"policy", "cost", "accuracy", "p95", "vs random", "cost saved")
	s += repeat("-", 82) + "\n"
	for _, p := range points {
		lift := l.Lift(p)
		marker := ""
		switch {
		case lift > 0.005:
			marker = fmt.Sprintf("%+.1f pts", lift*100)
		case lift < -0.005:
			marker = fmt.Sprintf("%+.1f pts", lift*100)
		default:
			marker = "no better"
		}
		save := l.Savings(p)
		saveStr := "-"
		if math.Abs(save) > 0.005 {
			saveStr = fmt.Sprintf("%+.0f%%", save*100)
		}
		s += fmt.Sprintf("%-22s %10.0f¢ %9.1f%% %9.0fms %12s %11s\n",
			p.Name, p.CostCents, p.Accuracy*100, p.P95Ms, marker, saveStr)
	}
	return s
}

// Plot draws the frontier as ASCII, because a picture of a Pareto frontier is
// worth more in a design review than the table it came from.
func Plot(points []Point, h Hull, width, height int) string {
	minC, maxC := math.Inf(1), math.Inf(-1)
	minA, maxA := math.Inf(1), math.Inf(-1)
	for _, p := range points {
		minC = math.Min(minC, p.CostCents)
		maxC = math.Max(maxC, p.CostCents)
		minA = math.Min(minA, p.Accuracy)
		maxA = math.Max(maxA, p.Accuracy)
	}
	if maxC == minC || maxA == minA {
		return ""
	}
	grid := make([][]rune, height)
	for i := range grid {
		grid[i] = []rune(repeat(" ", width))
	}
	col := func(c float64) int {
		x := int((c - minC) / (maxC - minC) * float64(width-1))
		return clampInt(x, 0, width-1)
	}
	row := func(a float64) int {
		y := int((maxA - a) / (maxA - minA) * float64(height-1))
		return clampInt(y, 0, height-1)
	}
	// The zero-information baseline first, so policy markers overwrite it.
	for x := 0; x < width; x++ {
		c := minC + (maxC-minC)*float64(x)/float64(width-1)
		grid[row(h.AccuracyAt(c))][x] = '.'
	}
	labels := []string{}
	for i, p := range points {
		mark := rune('A' + i)
		grid[row(p.Accuracy)][col(p.CostCents)] = mark
		labels = append(labels, fmt.Sprintf("  %c  %-22s %6.0f¢  %5.1f%%  (%+.1f pts)",
			mark, p.Name, p.CostCents, p.Accuracy*100, h.Lift(p)*100))
	}
	pad := 14
	out := ""
	for i, g := range grid {
		switch i {
		case 0:
			out += fmt.Sprintf("%*.1f%% ┤", pad-2, maxA*100) + string(g) + "\n"
		case height - 1:
			out += fmt.Sprintf("%*.1f%% ┤", pad-2, minA*100) + string(g) + "\n"
		default:
			out += repeat(" ", pad) + "│" + string(g) + "\n"
		}
	}
	out += repeat(" ", pad) + "└" + repeat("─", width) + "\n"
	left := fmt.Sprintf("%.0f", minC)
	right := fmt.Sprintf("%.0f", maxC)
	out += repeat(" ", pad+1) + left + repeat(" ", maxInt(1, width-len(left)-len(right))) + right + "  (cents)\n"
	out += "\n  '.' is the zero-information baseline (best random mix of fixed models)\n\n"
	for _, l := range labels {
		out += l + "\n"
	}
	return out
}

func maxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}

func clampInt(v, lo, hi int) int {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

func repeat(s string, n int) string {
	if n < 0 {
		n = 0
	}
	out := make([]byte, 0, n*len(s))
	for i := 0; i < n; i++ {
		out = append(out, s...)
	}
	return string(out)
}
