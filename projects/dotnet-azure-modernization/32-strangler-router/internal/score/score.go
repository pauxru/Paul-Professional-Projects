// Package score evaluates a ruleset against a corpus with known ground truth.
//
// A ruleset is a binary classifier over response pairs: "this pair diverges" or
// "this pair is fine". Treating it as one — and reporting precision and recall
// rather than a single accuracy number — is the whole idea, because the two
// error modes have wildly different costs.
//
// A false positive costs an engineer ten minutes.
// A false negative ships a defect and hides it behind a green dashboard.
package score

import (
	"fmt"
	"sort"

	"strangler/internal/corpus"
	"strangler/internal/jsondiff"
)

// Result is the confusion matrix plus the per-defect breakdown.
type Result struct {
	Name string
	TP   int
	FP   int
	TN   int
	FN   int
	// Missed counts, per defect, how many instances the ruleset failed to flag.
	Missed map[corpus.Defect]int
	// Seen counts, per defect, how many instances were present.
	Seen map[corpus.Defect]int
}

func (r Result) Precision() float64 {
	if r.TP+r.FP == 0 {
		return 1
	}
	return float64(r.TP) / float64(r.TP+r.FP)
}

func (r Result) Recall() float64 {
	if r.TP+r.FN == 0 {
		return 1
	}
	return float64(r.TP) / float64(r.TP+r.FN)
}

func (r Result) F1() float64 {
	p, rc := r.Precision(), r.Recall()
	if p+rc == 0 {
		return 0
	}
	return 2 * p * rc / (p + rc)
}

// NoiseFlagged is the fraction of clean pairs the ruleset reported as diverging
// — the number that determines whether anyone reads the report at all.
func (r Result) NoiseFlagged() float64 {
	if r.FP+r.TN == 0 {
		return 0
	}
	return float64(r.FP) / float64(r.FP+r.TN)
}

// MissedDefects lists the defects with at least one miss, worst first.
func (r Result) MissedDefects() []corpus.Defect {
	var out []corpus.Defect
	for d, n := range r.Missed {
		if n > 0 {
			out = append(out, d)
		}
	}
	sort.Slice(out, func(i, j int) bool {
		if r.Missed[out[i]] != r.Missed[out[j]] {
			return r.Missed[out[i]] > r.Missed[out[j]]
		}
		return out[i] < out[j]
	})
	return out
}

// Evaluate scores one ruleset over a corpus.
func Evaluate(name string, rules []jsondiff.Rule, pairs []corpus.Pair) (Result, error) {
	rs, err := jsondiff.Compile(rules)
	if err != nil {
		return Result{}, fmt.Errorf("%s: %w", name, err)
	}
	res := Result{Name: name, Missed: map[corpus.Defect]int{}, Seen: map[corpus.Defect]int{}}
	for _, p := range pairs {
		diffs := jsondiff.CompareBytes(p.Legacy, p.Modern, rs)
		flagged := len(diffs) > 0
		res.Seen[p.Defect]++
		switch {
		case p.Defective() && flagged:
			res.TP++
		case p.Defective() && !flagged:
			res.FN++
			res.Missed[p.Defect]++
		case !p.Defective() && flagged:
			res.FP++
		default:
			res.TN++
		}
	}
	return res, nil
}

// Table renders results as a fixed-width comparison.
func Table(results []Result) string {
	s := fmt.Sprintf("%-16s %7s %7s %7s %7s %10s %9s %8s %12s\n",
		"ruleset", "TP", "FP", "FN", "TN", "precision", "recall", "F1", "noise flagged")
	s += repeat("-", 96) + "\n"
	for _, r := range results {
		s += fmt.Sprintf("%-16s %7d %7d %7d %7d %9.3f %9.3f %8.3f %11.1f%%\n",
			r.Name, r.TP, r.FP, r.FN, r.TN,
			r.Precision(), r.Recall(), r.F1(), r.NoiseFlagged()*100)
	}
	return s
}

// PerDefect renders a defect-by-ruleset detection grid. This is the table that
// changes minds: an aggregate recall of 0.7 sounds tolerable until you can see
// which 30%.
func PerDefect(results []Result) string {
	defects := append([]corpus.Defect{}, corpus.AllDefects...)
	s := fmt.Sprintf("%-28s", "defect")
	for _, r := range results {
		s += fmt.Sprintf(" %14s", r.Name)
	}
	s += "\n" + repeat("-", 28+15*len(results)) + "\n"
	for _, d := range defects {
		s += fmt.Sprintf("%-28s", d)
		for _, r := range results {
			seen := r.Seen[d]
			missed := r.Missed[d]
			if seen == 0 {
				s += fmt.Sprintf(" %14s", "-")
				continue
			}
			if missed == 0 {
				s += fmt.Sprintf(" %14s", "caught")
			} else {
				s += fmt.Sprintf(" %14s", fmt.Sprintf("MISSED %d/%d", missed, seen))
			}
		}
		s += "\n"
	}
	return s
}

func repeat(s string, n int) string {
	out := make([]byte, 0, n)
	for i := 0; i < n; i++ {
		out = append(out, s...)
	}
	return string(out)
}
