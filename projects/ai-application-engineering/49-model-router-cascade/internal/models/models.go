// Package models is a deterministic simulator for a fleet of language models.
//
// It is a simulator, and the README says so plainly. No LLM is called. What is
// being measured here is *the routing algorithm*, and for that the simulator
// only has to be honest about the shape of the problem:
//
//   - a bigger model is more likely to be right, and costs more
//   - how much more likely depends on how hard the question is
//   - a model's self-reported confidence is not the same thing as its
//     probability of being right
//
// That last point is the one everything turns on, and it is the one a router
// built against real providers has to confront too.
package models

import (
	"fmt"
	"math"

	"router/internal/workload"
)

// Model is one endpoint in the fleet.
type Model struct {
	Name string
	// Competence is the difficulty at which the model is right half the time.
	Competence float64
	// Sharpness is how quickly accuracy falls off around Competence.
	Sharpness float64
	// CostPerKTok in cents, input and output pooled for simplicity.
	CostPerKTok float64
	// LatencyMs is the median; the simulator adds lognormal jitter.
	LatencyMs float64
	// Calibration distorts the confidence the model reports about its own
	// answer. 1.0 is perfectly calibrated. Above 1 is overconfident: the model
	// reports 0.95 when it is right 0.80 of the time, which is the normal state
	// of affairs for a real model and the reason cascades disappoint.
	//
	// Concretely it multiplies the log-odds of the honest belief, so a value of
	// 1.9 means a temperature of 1.9 is exactly what recovers honesty. Section 4
	// of the report fits that temperature from data and recovers this number,
	// which is the check that the calibration machinery works at all.
	Calibration float64
}

// Accuracy is the probability this model answers a query of difficulty d
// correctly.
func (m Model) Accuracy(d float64) float64 {
	return 1 / (1 + math.Exp(-m.Sharpness*(m.Competence-d)))
}

// Cost in cents for a query.
func (m Model) Cost(q workload.Query) float64 {
	return m.CostPerKTok * float64(q.Tokens) / 1000
}

// Fleet is the default three-tier fleet: a small local model, a mid-tier hosted
// model, and a frontier model at twenty times the price.
func Fleet() []Model {
	return []Model{
		{Name: "small", Competence: 0.34, Sharpness: 9, CostPerKTok: 0.02, LatencyMs: 120, Calibration: 1.9},
		{Name: "mid", Competence: 0.58, Sharpness: 8, CostPerKTok: 0.30, LatencyMs: 420, Calibration: 1.4},
		{Name: "large", Competence: 0.86, Sharpness: 7, CostPerKTok: 2.00, LatencyMs: 1500, Calibration: 1.1},
	}
}

// Answer is the outcome of one call.
type Answer struct {
	Model string
	// Correct is ground truth, available only because this is a simulation. In
	// production it comes from an eval set, thumbs-down signals, or a judge.
	Correct bool
	// Confidence is what the model reports about its own answer. This is the
	// only quality signal a cascade is allowed to act on.
	Confidence float64
	CostCents  float64
	LatencyMs  float64
}

// Oracle answers deterministically for a given (query, model, seed), so that
// two routing policies that make the same call on the same query get the same
// answer. Without this the comparison between policies would be measuring
// sampling noise as well as routing quality — with 4,000 queries and a 3-point
// difference to detect, that noise dominates.
type Oracle struct {
	fleet map[string]Model
	seed  uint64
}

func NewOracle(fleet []Model, seed uint64) *Oracle {
	m := map[string]Model{}
	for _, f := range fleet {
		m[f.Name] = f
	}
	return &Oracle{fleet: m, seed: seed}
}

// Ask returns the answer this model would give to this query. It is a pure
// function of (query id, model name, seed).
func (o *Oracle) Ask(q workload.Query, name string) Answer {
	m, ok := o.fleet[name]
	if !ok {
		panic("unknown model " + name)
	}
	r := workload.NewRng(mix(o.seed, uint64(q.ID), hash(name)))

	acc := m.Accuracy(q.Difficulty)
	correct := r.Float() < acc

	// The model's honest belief is its true probability of being right, blurred
	// by the fact that it cannot see its own accuracy either.
	honest := clamp(acc+0.08*r.Normal(), 0.01, 0.99)
	// A model that is wrong still often believes it is right, and that belief is
	// what makes overconfidence dangerous rather than merely inaccurate.
	if !correct {
		honest = clamp(honest-0.15*math.Abs(r.Normal()), 0.01, 0.99)
	}
	conf := distort(honest, m.Calibration)

	lat := m.LatencyMs * math.Exp(0.25*r.Normal())
	return Answer{
		Model:      m.Name,
		Correct:    correct,
		Confidence: conf,
		CostCents:  m.Cost(q),
		LatencyMs:  lat,
	}
}

// Model looks up a model by name.
func (o *Oracle) Model(name string) Model { return o.fleet[name] }

// WouldBeCorrect is ground truth, used only by the oracle routing baseline and
// by the calibration measurement. A routing policy must never call it.
func (o *Oracle) WouldBeCorrect(q workload.Query, name string) bool {
	return o.Ask(q, name).Correct
}

// distort applies a calibration temperature by MULTIPLYING the log-odds of the
// honest belief by t. t > 1 therefore pushes probabilities towards 0 and 1 —
// overconfidence. t < 1 pulls them towards 0.5.
//
// The sign convention is the one that matters: calibration.Temper divides the
// log-odds, so calibration.Temper(distort(p, t), t) == p. The simulator's
// Calibration field and the fitted recalibration temperature are the same
// number, and TestFittedTemperatureRecoversCalibration asserts it.
func distort(p, t float64) float64 {
	if t == 1 {
		return p
	}
	return clamp(math.Pow(p, t)/(math.Pow(p, t)+math.Pow(1-p, t)), 0.001, 0.999)
}

// Recalibrate returns a copy of the fleet with every model perfectly calibrated,
// which is the counterfactual the experiment needs: the same models, the same
// accuracy, the same cost, and an honest confidence signal.
func Recalibrate(fleet []Model) []Model {
	out := make([]Model, len(fleet))
	copy(out, fleet)
	for i := range out {
		out[i].Calibration = 1.0
	}
	return out
}

// Describe renders the fleet for the report.
func Describe(fleet []Model) string {
	s := fmt.Sprintf("%-8s %11s %13s %10s %14s\n",
		"model", "competence", "cost/1k tok", "latency", "calibration")
	s += repeat("-", 60) + "\n"
	for _, m := range fleet {
		s += fmt.Sprintf("%-8s %11.2f %11.2f¢ %8.0fms %14.2f\n",
			m.Name, m.Competence, m.CostPerKTok, m.LatencyMs, m.Calibration)
	}
	return s
}

func mix(a, b, c uint64) uint64 {
	h := a ^ (b * 0x9E3779B97F4A7C15) ^ (c * 0xC2B2AE3D27D4EB4F)
	h ^= h >> 33
	h *= 0xFF51AFD7ED558CCD
	h ^= h >> 33
	return h
}

func hash(s string) uint64 {
	var h uint64 = 1469598103934665603
	for i := 0; i < len(s); i++ {
		h ^= uint64(s[i])
		h *= 1099511628211
	}
	return h
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
