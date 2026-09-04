// Package router holds the routing policies and the baselines they must beat.
//
// The baselines are the important part. A router that sends 30% of traffic to
// the large model and reports "82% accuracy at 40% of the cost" has said
// nothing until you know what *random* 30% escalation achieves. Any policy that
// cannot beat a coin flip at the same spend is not adding information, however
// sophisticated it looks.
package router

import (
	"math"
	"sort"

	"router/internal/models"
	"router/internal/workload"
)

// Result is what one policy did to one query.
type Result struct {
	Query      workload.Query
	Correct    bool
	CostCents  float64
	LatencyMs  float64
	Calls      []string
	Escalated  bool
	Confidence float64
}

// Outcome aggregates a policy run.
type Outcome struct {
	Name       string
	Results    []Result
	Accuracy   float64
	CostCents  float64
	P50Latency float64
	P95Latency float64
	EscalRate  float64
	CallsPerQ  float64
}

// Policy decides how to answer a query. It is given the oracle only so it can
// *call* models; reading ground truth from it is cheating and only the oracle
// baseline does it.
type Policy interface {
	Name() string
	Route(q workload.Query, o *models.Oracle) Result
}

// As relabels a policy, so a threshold sweep can report each operating point
// under its own name without every policy carrying a label field.
func As(name string, p Policy) Policy { return named{name, p} }

type named struct {
	name string
	p    Policy
}

func (n named) Name() string { return n.name }

func (n named) Route(q workload.Query, o *models.Oracle) Result { return n.p.Route(q, o) }

// Run executes a policy over a workload and aggregates.
func Run(p Policy, qs []workload.Query, o *models.Oracle) Outcome {
	out := Outcome{Name: p.Name(), Results: make([]Result, 0, len(qs))}
	if len(qs) == 0 {
		return out
	}
	correct, cost, calls, escal := 0, 0.0, 0, 0
	lat := make([]float64, 0, len(qs))
	for _, q := range qs {
		r := p.Route(q, o)
		if r.Correct {
			correct++
		}
		cost += r.CostCents
		calls += len(r.Calls)
		if r.Escalated {
			escal++
		}
		lat = append(lat, r.LatencyMs)
		out.Results = append(out.Results, r)
	}
	n := float64(len(qs))
	out.Accuracy = float64(correct) / n
	out.CostCents = cost
	out.EscalRate = float64(escal) / n
	out.CallsPerQ = float64(calls) / n
	sort.Float64s(lat)
	out.P50Latency = pct(lat, 0.50)
	out.P95Latency = pct(lat, 0.95)
	return out
}

func pct(sorted []float64, p float64) float64 {
	if len(sorted) == 0 {
		return 0
	}
	i := int(p * float64(len(sorted)-1))
	return sorted[i]
}

// ---------------------------------------------------------------- baselines

// Single always calls one model. The two endpoints of every frontier.
type Single struct{ Model string }

func (s Single) Name() string { return "always-" + s.Model }

func (s Single) Route(q workload.Query, o *models.Oracle) Result {
	a := o.Ask(q, s.Model)
	return Result{Query: q, Correct: a.Correct, CostCents: a.CostCents,
		LatencyMs: a.LatencyMs, Calls: []string{s.Model}, Confidence: a.Confidence}
}

// Random escalates a fixed fraction of traffic chosen at random. This is the
// baseline that matters: it traces the straight line between the two endpoints
// in cost/quality space, and it is exactly what a router achieves if its
// signal carries no information. Beating "always-large" on cost is trivial.
// Beating Random at the same cost is the whole job.
type Random struct {
	Small, Large string
	Rate         float64
	Seed         uint64
}

func (r Random) Name() string { return "random" }

func (r Random) Route(q workload.Query, o *models.Oracle) Result {
	rng := workload.NewRng(r.Seed ^ uint64(q.ID)*0x9E3779B97F4A7C15)
	if rng.Float() < r.Rate {
		a := o.Ask(q, r.Large)
		return Result{Query: q, Correct: a.Correct, CostCents: a.CostCents,
			LatencyMs: a.LatencyMs, Calls: []string{r.Large}, Escalated: true}
	}
	a := o.Ask(q, r.Small)
	return Result{Query: q, Correct: a.Correct, CostCents: a.CostCents,
		LatencyMs: a.LatencyMs, Calls: []string{r.Small}}
}

// Oracle escalates only when the small model would be wrong and the large model
// would be right. Unimplementable, and that is the point: it is the ceiling
// that says how much headroom any router has.
type Oracle struct{ Small, Large string }

func (Oracle) Name() string { return "oracle" }

func (op Oracle) Route(q workload.Query, o *models.Oracle) Result {
	small := o.Ask(q, op.Small)
	if small.Correct {
		return Result{Query: q, Correct: true, CostCents: small.CostCents,
			LatencyMs: small.LatencyMs, Calls: []string{op.Small}}
	}
	large := o.Ask(q, op.Large)
	if !large.Correct {
		// Escalating would burn the money and still be wrong, so do not.
		return Result{Query: q, Correct: false, CostCents: small.CostCents,
			LatencyMs: small.LatencyMs, Calls: []string{op.Small}}
	}
	return Result{Query: q, Correct: true, CostCents: large.CostCents,
		LatencyMs: large.LatencyMs, Calls: []string{op.Large}, Escalated: true}
}

// ---------------------------------------------------------------- learned

// Classifier predicts difficulty from surface features and escalates above a
// threshold. It decides *before* spending anything, so it never pays twice —
// and it never gets to see how the cheap model actually did.
type Classifier struct {
	Small, Large string
	Weights      []float64
	Threshold    float64
}

func (Classifier) Name() string { return "classifier" }

func (c Classifier) Score(q workload.Query) float64 {
	return sigmoid(dot(c.Weights, q.Features()))
}

func (c Classifier) Route(q workload.Query, o *models.Oracle) Result {
	if c.Score(q) >= c.Threshold {
		a := o.Ask(q, c.Large)
		return Result{Query: q, Correct: a.Correct, CostCents: a.CostCents,
			LatencyMs: a.LatencyMs, Calls: []string{c.Large}, Escalated: true}
	}
	a := o.Ask(q, c.Small)
	return Result{Query: q, Correct: a.Correct, CostCents: a.CostCents,
		LatencyMs: a.LatencyMs, Calls: []string{c.Small}}
}

// TrainClassifier fits logistic regression to predict "the small model gets
// this wrong" from observable features. Plain batch gradient descent: the model
// is five weights and the point is the routing, not the optimiser.
func TrainClassifier(train []workload.Query, o *models.Oracle, small, large string,
	threshold float64, epochs int, lr float64) Classifier {

	if len(train) == 0 {
		return Classifier{Small: small, Large: large, Weights: []float64{0, 0, 0, 0, 0}, Threshold: threshold}
	}
	dim := len(train[0].Features())
	w := make([]float64, dim)
	labels := make([]float64, len(train))
	feats := make([][]float64, len(train))
	for i, q := range train {
		feats[i] = q.Features()
		if !o.WouldBeCorrect(q, small) {
			labels[i] = 1
		}
	}
	for e := 0; e < epochs; e++ {
		grad := make([]float64, dim)
		for i := range train {
			p := sigmoid(dot(w, feats[i]))
			err := p - labels[i]
			for j := range grad {
				grad[j] += err * feats[i][j]
			}
		}
		for j := range w {
			w[j] -= lr * grad[j] / float64(len(train))
		}
	}
	return Classifier{Small: small, Large: large, Weights: w, Threshold: threshold}
}

// Cascade calls the cheap model first and escalates when its self-reported
// confidence is below a threshold. It has strictly more information than the
// classifier — it has seen an actual attempt — and it pays for that information
// on every query it escalates.
type Cascade struct {
	Small, Large string
	Threshold    float64
	// Recalibrate applies a fitted temperature to the reported confidence
	// before comparing it to the threshold.
	Temperature float64
}

func (Cascade) Name() string { return "cascade" }

func (c Cascade) Route(q workload.Query, o *models.Oracle) Result {
	small := o.Ask(q, c.Small)
	conf := small.Confidence
	if c.Temperature > 0 && c.Temperature != 1 {
		conf = temper(conf, c.Temperature)
	}
	if conf >= c.Threshold {
		return Result{Query: q, Correct: small.Correct, CostCents: small.CostCents,
			LatencyMs: small.LatencyMs, Calls: []string{c.Small}, Confidence: conf}
	}
	large := o.Ask(q, c.Large)
	return Result{
		Query:      q,
		Correct:    large.Correct,
		CostCents:  small.CostCents + large.CostCents,
		LatencyMs:  small.LatencyMs + large.LatencyMs,
		Calls:      []string{c.Small, c.Large},
		Escalated:  true,
		Confidence: conf,
	}
}

// ValueCascade escalates on expected value per cent rather than on a fixed
// confidence threshold.
//
// This exists because of an asymmetry a fixed threshold cannot see. Escalation
// cost is proportional to prompt size, and prompt size varies by two orders of
// magnitude across a real workload. A fixed threshold spends the same
// confidence budget on a 40-token lookup and a 4,000-token document, but the
// second costs a hundred times more to escalate. The decision is not "am I
// unsure enough" — it is "is what I would learn worth what it costs".
//
// Escalate when (PLarge - confidence) / marginal_cost >= Lambda.
//
// Note what this does to the confidence signal: it is now used *arithmetically*
// rather than compared to a threshold. That is the difference that makes
// calibration load-bearing here and irrelevant for Cascade.
type ValueCascade struct {
	Small, Large string
	// PLarge is the large model's accuracy, estimated on the training split.
	// A router in production would get this from an eval set.
	PLarge float64
	// Lambda is accuracy points per cent of marginal spend. It is a price, and
	// unlike a confidence threshold it is a quantity a finance conversation can
	// actually be had about.
	Lambda float64
	// Temperature recalibrates the confidence before it is used in arithmetic.
	Temperature float64
}

func (ValueCascade) Name() string { return "value-cascade" }

func (c ValueCascade) Route(q workload.Query, o *models.Oracle) Result {
	small := o.Ask(q, c.Small)
	conf := small.Confidence
	if c.Temperature > 0 && c.Temperature != 1 {
		conf = temper(conf, c.Temperature)
	}
	gain := c.PLarge - conf
	marginal := o.Model(c.Large).Cost(q)
	if marginal <= 0 || gain/marginal < c.Lambda {
		return Result{Query: q, Correct: small.Correct, CostCents: small.CostCents,
			LatencyMs: small.LatencyMs, Calls: []string{c.Small}, Confidence: conf}
	}
	large := o.Ask(q, c.Large)
	return Result{
		Query:      q,
		Correct:    large.Correct,
		CostCents:  small.CostCents + large.CostCents,
		LatencyMs:  small.LatencyMs + large.LatencyMs,
		Calls:      []string{c.Small, c.Large},
		Escalated:  true,
		Confidence: conf,
	}
}

// EstimatePLarge measures the large model's accuracy on the training split.
func EstimatePLarge(train []workload.Query, o *models.Oracle, large string) float64 {
	if len(train) == 0 {
		return 0
	}
	n := 0
	for _, q := range train {
		if o.WouldBeCorrect(q, large) {
			n++
		}
	}
	return float64(n) / float64(len(train))
}

// Cascade3 is a three-stage chain: try cheap, then mid, then frontier, stopping
// as soon as a stage clears its own threshold.
//
// Worth building only if the middle model is on the fleet's convex hull. If it
// is not, the middle stage is a pure tax: you pay for a call that the hull says
// you could have replaced with a coin flip. Section 3 of the report checks that
// before this type is used at all.
type Cascade3 struct {
	A, B, C     string
	ThreshA     float64
	ThreshB     float64
	Temperature float64
}

func (Cascade3) Name() string { return "cascade3" }

func (c Cascade3) Route(q workload.Query, o *models.Oracle) Result {
	a := o.Ask(q, c.A)
	confA := c.conf(a.Confidence)
	if confA >= c.ThreshA {
		return Result{Query: q, Correct: a.Correct, CostCents: a.CostCents,
			LatencyMs: a.LatencyMs, Calls: []string{c.A}, Confidence: confA}
	}
	b := o.Ask(q, c.B)
	confB := c.conf(b.Confidence)
	if confB >= c.ThreshB {
		return Result{Query: q, Correct: b.Correct, CostCents: a.CostCents + b.CostCents,
			LatencyMs: a.LatencyMs + b.LatencyMs, Calls: []string{c.A, c.B},
			Escalated: true, Confidence: confB}
	}
	d := o.Ask(q, c.C)
	return Result{Query: q, Correct: d.Correct,
		CostCents: a.CostCents + b.CostCents + d.CostCents,
		LatencyMs: a.LatencyMs + b.LatencyMs + d.LatencyMs,
		Calls:     []string{c.A, c.B, c.C}, Escalated: true, Confidence: confB}
}

func (c Cascade3) conf(v float64) float64 {
	if c.Temperature > 0 && c.Temperature != 1 {
		return temper(v, c.Temperature)
	}
	return v
}

// temper inverts an overconfidence distortion. t > 1 pulls probabilities back
// towards 0.5.
//
// It is monotone in p, and temper(·, 1/t) is its exact inverse — logit(temper(p,t))
// is just logit(p)/t. Both facts matter: see ADR 0003.
func temper(p, t float64) float64 {
	a := math.Pow(p, 1/t)
	b := math.Pow(1-p, 1/t)
	return a / (a + b)
}

// Temper is exported for the tests that pin the reparameterisation identity.
func Temper(p, t float64) float64 { return temper(p, t) }

func sigmoid(x float64) float64 { return 1 / (1 + math.Exp(-x)) }

func dot(a, b []float64) float64 {
	s := 0.0
	for i := range a {
		if i < len(b) {
			s += a[i] * b[i]
		}
	}
	return s
}
