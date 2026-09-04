// Package workload generates a labelled query stream with a latent difficulty.
//
// The difficulty is the ground truth that makes the whole experiment possible:
// it decides whether each model answers correctly, and it is deliberately *not*
// visible to any router. Routers see only the surface features, exactly as they
// would in production.
package workload

import (
	"fmt"
	"math"
	"sort"
)

// Rng is SplitMix64. Every number in docs/results.md is a pure function of a
// seed, so the results file reproduces byte for byte.
type Rng struct{ s uint64 }

func NewRng(seed uint64) *Rng { return &Rng{s: seed} }

func (r *Rng) Next() uint64 {
	r.s += 0x9E3779B97F4A7C15
	z := r.s
	z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9
	z = (z ^ (z >> 27)) * 0x94D049BB133111EB
	return z ^ (z >> 31)
}

// Float returns a value in [0, 1).
func (r *Rng) Float() float64 { return float64(r.Next()>>11) / float64(1<<53) }

func (r *Rng) Below(n int) int { return int(r.Next() % uint64(n)) }

// Normal returns a standard normal draw (Box-Muller).
func (r *Rng) Normal() float64 {
	u1 := r.Float()
	if u1 < 1e-12 {
		u1 = 1e-12
	}
	u2 := r.Float()
	return math.Sqrt(-2*math.Log(u1)) * math.Cos(2*math.Pi*u2)
}

// Class is the kind of question, which is what a production system can actually
// observe about a request before answering it.
type Class string

const (
	Lookup    Class = "lookup"    // "what is our refund window"
	Summarise Class = "summarise" // "summarise this thread"
	Reasoning Class = "reasoning" // "does this contract permit X given Y"
	Code      Class = "code"      // "why does this function deadlock"
	Ambiguous Class = "ambiguous" // underspecified; hard for everyone
)

var Classes = []Class{Lookup, Summarise, Reasoning, Code, Ambiguous}

// classProfile is the latent difficulty distribution for each class. These are
// the generative truth; a router that knew them exactly would still not have an
// oracle, because difficulty varies within a class.
var classProfile = map[Class]struct {
	share    float64
	meanDiff float64
	sdDiff   float64
}{
	Lookup:    {0.34, 0.18, 0.10},
	Summarise: {0.26, 0.36, 0.14},
	Reasoning: {0.18, 0.62, 0.16},
	Code:      {0.14, 0.68, 0.18},
	Ambiguous: {0.08, 0.80, 0.15},
}

// Query is one request. Difficulty is hidden from routers.
type Query struct {
	ID     int
	Tenant string
	Class  Class
	// Tokens is the total prompt size, which drives cost.
	Tokens int
	// ContextTokens is attached material — a pasted log, a contract, a thread.
	// It is drawn INDEPENDENTLY of difficulty, and that independence is the
	// point of the whole workload: cost varies by two orders of magnitude for
	// reasons that have nothing to do with whether the small model can cope.
	// A router that treats every query's escalation as equally worth paying for
	// is ignoring the largest term in its own bill.
	ContextTokens int
	// Words, HasCode and QuestionMarks are surface features a router may use.
	Words         int
	HasCode       bool
	QuestionMarks int

	// Difficulty is the latent truth. Routers must not read this; only the
	// simulator and the oracle baseline may.
	Difficulty float64
}

// Features is the observable vector a routing policy is allowed to use.
func (q Query) Features() []float64 {
	code := 0.0
	if q.HasCode {
		code = 1
	}
	return []float64{
		1,
		math.Log1p(float64(q.Words)) / 8,
		code,
		float64(q.QuestionMarks) / 3,
		math.Log1p(float64(q.ContextTokens)) / 10,
	}
}

var tenants = []string{"acme", "globex", "initech"}

// Generate produces n queries. The class mix and the difficulty within each
// class are both stochastic, so a router cannot win by memorising the class.
func Generate(n int, seed uint64) []Query {
	r := NewRng(seed)
	cum := make([]float64, 0, len(Classes))
	total := 0.0
	for _, c := range Classes {
		total += classProfile[c].share
		cum = append(cum, total)
	}

	out := make([]Query, 0, n)
	for i := 0; i < n; i++ {
		u := r.Float() * total
		idx := sort.SearchFloat64s(cum, u)
		if idx >= len(Classes) {
			idx = len(Classes) - 1
		}
		cl := Classes[idx]
		p := classProfile[cl]

		d := p.meanDiff + p.sdDiff*r.Normal()
		d = clamp(d, 0.01, 0.99)

		// Surface features correlate with difficulty but do not determine it.
		// This is the point: a classifier can learn something real from them and
		// still be far from the oracle.
		words := int(20 + 220*d + 40*r.Normal())
		if words < 3 {
			words = 3
		}

		// Attached context. Drawn independently of difficulty, because in a real
		// support or document workload the person who pastes a 40-page contract
		// is often asking the easiest question in the queue.
		ctx := 0
		if r.Float() < 0.38 {
			ctx = int(math.Exp(6.2 + 1.15*r.Normal()))
			if ctx > 60000 {
				ctx = 60000
			}
		}

		q := Query{
			ID:            i,
			Tenant:        tenants[r.Below(len(tenants))],
			Class:         cl,
			Words:         words,
			HasCode:       cl == Code || (cl == Reasoning && r.Float() < 0.2),
			QuestionMarks: 1 + r.Below(1+int(3*d)),
			ContextTokens: ctx,
			Tokens:        int(float64(words)*1.4) + 40 + ctx,
			Difficulty:    d,
		}
		out = append(out, q)
	}
	return out
}

// Split partitions a workload into a training half and an evaluation half.
// Every learned policy is fitted on the first and scored on the second; a
// router evaluated on its training data measures nothing.
func Split(qs []Query, trainFrac float64) (train, eval []Query) {
	cut := int(float64(len(qs)) * trainFrac)
	return qs[:cut], qs[cut:]
}

// Summary is a human-readable description of the mix, used in the report.
func Summary(qs []Query) string {
	counts := map[Class]int{}
	diff := map[Class]float64{}
	for _, q := range qs {
		counts[q.Class]++
		diff[q.Class] += q.Difficulty
	}
	s := fmt.Sprintf("%-12s %8s %8s %14s %14s\n", "class", "count", "share", "mean difficulty", "median tokens")
	s += repeat("-", 62) + "\n"
	for _, c := range Classes {
		n := counts[c]
		if n == 0 {
			continue
		}
		var toks []float64
		for _, q := range qs {
			if q.Class == c {
				toks = append(toks, float64(q.Tokens))
			}
		}
		sort.Float64s(toks)
		s += fmt.Sprintf("%-12s %8d %7.1f%% %14.3f %14.0f\n",
			c, n, float64(n)/float64(len(qs))*100, diff[c]/float64(n), toks[len(toks)/2])
	}
	return s
}

// CostDifficultyCorrelation is the Pearson correlation between log token count
// and latent difficulty.
//
// It is reported because the entire argument for cost-aware routing rests on it
// being near zero. If prompt size predicted difficulty, a fixed confidence
// threshold would already be doing the right thing by accident.
func CostDifficultyCorrelation(qs []Query) float64 {
	n := float64(len(qs))
	if n < 2 {
		return 0
	}
	var mx, my float64
	for _, q := range qs {
		mx += math.Log(float64(q.Tokens))
		my += q.Difficulty
	}
	mx, my = mx/n, my/n
	var sxy, sxx, syy float64
	for _, q := range qs {
		dx := math.Log(float64(q.Tokens)) - mx
		dy := q.Difficulty - my
		sxy += dx * dy
		sxx += dx * dx
		syy += dy * dy
	}
	if sxx == 0 || syy == 0 {
		return 0
	}
	return sxy / math.Sqrt(sxx*syy)
}

// TokenPercentiles returns the p1/p50/p99 token counts, which is how the cost
// spread gets quoted in the report.
func TokenPercentiles(qs []Query) (p1, p50, p99 int) {
	t := make([]int, len(qs))
	for i, q := range qs {
		t[i] = q.Tokens
	}
	sort.Ints(t)
	at := func(p float64) int { return t[int(p*float64(len(t)-1))] }
	return at(0.01), at(0.50), at(0.99)
}

func clamp(v, lo, hi float64) float64 {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

func repeat(s string, n int) string {
	out := make([]byte, 0, n*len(s))
	for i := 0; i < n; i++ {
		out = append(out, s...)
	}
	return string(out)
}
