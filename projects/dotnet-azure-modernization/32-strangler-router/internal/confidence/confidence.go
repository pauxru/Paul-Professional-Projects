// Package confidence turns a stream of shadow comparisons into a promotion
// decision.
//
// The obvious implementation is a counter: matched / total, promote above 99%.
// It is wrong in a specific and expensive way.
//
// An endpoint that has been hit eleven times with eleven matches has an
// observed rate of 100%. So does an endpoint hit forty thousand times. A raw
// rate cannot distinguish "this is correct" from "we have not looked at it
// yet", and low-traffic endpoints are exactly where the interesting bugs live —
// the refund path, the B2B invoice path, the one admin screen.
//
// So promotion is gated on the **lower bound of a Wilson score interval**
// rather than the point estimate. With eleven successes out of eleven the lower
// bound at 99% confidence is about 0.68, nowhere near a promotion threshold.
// It reaches 0.99 only after several hundred consistent observations. The
// endpoint is not blocked because it failed; it is blocked because nobody has
// looked at it, and that is the honest reason.
package confidence

import (
	"fmt"
	"math"
	"sort"
	"sync"
	"time"
)

// Stage is where an endpoint sits in the migration.
type Stage string

const (
	// Shadow mirrors traffic to the modern implementation and discards its
	// response. Legacy always answers.
	Shadow Stage = "shadow"
	// Canary sends a fraction of real traffic to the modern implementation for
	// real, and shadows the rest.
	Canary Stage = "canary"
	// Cutover sends everything to the modern implementation.
	Cutover Stage = "cutover"
	// Halted means divergence was detected after promotion and the endpoint was
	// rolled back. It does not auto-recover.
	Halted Stage = "halted"
)

// Policy is the promotion and rollback configuration.
type Policy struct {
	// Confidence for the Wilson interval, e.g. 0.99.
	Z float64
	// PromoteAbove is the Wilson lower bound required to move up a stage.
	PromoteAbove float64
	// MinSamples is a floor applied in addition to the interval. The interval
	// alone would suffice mathematically; the floor exists because a handful of
	// requests can all be the same request.
	MinSamples int
	// RollbackWindow is how many recent comparisons the rollback check looks at.
	RollbackWindow int
	// RollbackFailures is how many divergences within that window trigger a
	// rollback.
	RollbackFailures int
	// CanaryPercent is the traffic fraction at the canary stage.
	CanaryPercent int
}

// DefaultPolicy is deliberately conservative. Every number here is a judgement
// call and each one is defended in docs/adr/0003-promotion-policy.md.
func DefaultPolicy() Policy {
	return Policy{
		Z:                2.576, // 99%
		PromoteAbove:     0.995,
		MinSamples:       200,
		RollbackWindow:   50,
		RollbackFailures: 3,
		CanaryPercent:    5,
	}
}

// WilsonLower is the lower bound of the Wilson score interval for k successes
// out of n trials.
//
// Chosen over the normal approximation because the normal approximation is
// badly wrong exactly where it matters here: near p=1 it produces an interval
// of zero width, so 40/40 successes would report a lower bound of 1.0 and
// promote immediately.
func WilsonLower(k, n int, z float64) float64 {
	if n == 0 {
		return 0
	}
	p := float64(k) / float64(n)
	nn := float64(n)
	denom := 1 + z*z/nn
	centre := p + z*z/(2*nn)
	margin := z * math.Sqrt(p*(1-p)/nn+z*z/(4*nn*nn))
	lower := (centre - margin) / denom
	if lower < 0 {
		return 0
	}
	return lower
}

// Observation is one shadow comparison result.
type Observation struct {
	At      time.Time
	Matched bool
	Detail  string
}

// Endpoint tracks one route through the migration.
//
// It keeps two sets of counters. total/matched are the lifetime record, which
// is what a human wants to see. stageTotal/stageMatched are the evidence
// gathered since entering the current stage, and that is what promotion is
// judged on.
//
// The split is deliberate. A spotless shadow record says the modern read path
// produces the same bytes; it says nothing about whether it survives being on
// the hot path, where connection pools, cache behaviour and timeouts are
// different. Carrying shadow evidence into the canary decision would let an
// endpoint cut over on the strength of samples taken under conditions that no
// longer apply.
type Endpoint struct {
	Route        string
	stage        Stage
	total        int
	matched      int
	stageTotal   int
	stageMatched int
	recent       []bool
	window       int
	// LastDivergence is kept for the report; a rollback is far more informative
	// with the response that caused it.
	LastDivergence string
	PromotedAt     map[Stage]int
}

func newEndpoint(route string, window int) *Endpoint {
	return &Endpoint{Route: route, stage: Shadow, window: window,
		PromotedAt: map[Stage]int{}}
}

func (e *Endpoint) Stage() Stage      { return e.stage }
func (e *Endpoint) Total() int        { return e.total }
func (e *Endpoint) Matched() int      { return e.matched }
func (e *Endpoint) StageTotal() int   { return e.stageTotal }
func (e *Endpoint) StageMatched() int { return e.stageMatched }
func (e *Endpoint) Rate() float64 {
	if e.total == 0 {
		return 0
	}
	return float64(e.matched) / float64(e.total)
}

// Tracker holds every endpoint and applies the policy.
type Tracker struct {
	mu     sync.Mutex
	policy Policy
	eps    map[string]*Endpoint
	// Events records every stage transition, which is the artefact a migration
	// review actually wants to look at.
	Events []Event
}

// Event is a stage transition with the evidence that caused it.
type Event struct {
	Route   string
	From    Stage
	To      Stage
	AtCount int
	Reason  string
}

func (e Event) String() string {
	return fmt.Sprintf("%-16s %-8s -> %-8s after %5d requests: %s",
		e.Route, e.From, e.To, e.AtCount, e.Reason)
}

func NewTracker(p Policy) *Tracker {
	return &Tracker{policy: p, eps: map[string]*Endpoint{}}
}

func (t *Tracker) Endpoint(route string) *Endpoint {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.endpointLocked(route)
}

func (t *Tracker) endpointLocked(route string) *Endpoint {
	e, ok := t.eps[route]
	if !ok {
		e = newEndpoint(route, t.policy.RollbackWindow)
		t.eps[route] = e
	}
	return e
}

// Record files one comparison result and applies promotion or rollback.
//
// Promotion and rollback are evaluated in the same call, and rollback is
// checked first. An endpoint that has just accumulated enough samples to
// promote *and* has three failures in its recent window must not be promoted on
// the strength of an average that includes ancient history.
func (t *Tracker) Record(route string, matched bool, detail string) Stage {
	t.mu.Lock()
	defer t.mu.Unlock()
	e := t.endpointLocked(route)
	e.total++
	e.stageTotal++
	if matched {
		e.matched++
		e.stageMatched++
	} else {
		e.LastDivergence = detail
	}
	e.recent = append(e.recent, matched)
	if len(e.recent) > e.window {
		e.recent = e.recent[len(e.recent)-e.window:]
	}

	failures := 0
	for _, ok := range e.recent {
		if !ok {
			failures++
		}
	}

	if failures >= t.policy.RollbackFailures {
		if e.stage == Canary || e.stage == Cutover {
			t.transition(e, Halted, fmt.Sprintf(
				"%d divergences in the last %d requests; %s",
				failures, len(e.recent), detail))
		}
		return e.stage
	}

	if e.stage == Halted || e.stage == Cutover {
		return e.stage
	}
	if e.stageTotal < t.policy.MinSamples {
		return e.stage
	}
	lower := WilsonLower(e.stageMatched, e.stageTotal, t.policy.Z)
	if lower < t.policy.PromoteAbove {
		return e.stage
	}
	switch e.stage {
	case Shadow:
		t.transition(e, Canary, fmt.Sprintf(
			"Wilson lower bound %.4f over %d requests in shadow",
			lower, e.stageTotal))
	case Canary:
		t.transition(e, Cutover, fmt.Sprintf(
			"Wilson lower bound %.4f over %d requests at %d%% canary",
			lower, e.stageTotal, t.policy.CanaryPercent))
	}
	return e.stage
}

func (t *Tracker) transition(e *Endpoint, to Stage, reason string) {
	t.Events = append(t.Events, Event{Route: e.Route, From: e.stage, To: to,
		AtCount: e.total, Reason: reason})
	e.stage = to
	e.PromotedAt[to] = e.total
	// A rollback clears the recent window: the endpoint has to earn its way back
	// with new evidence, not by ageing out the failures that halted it. The
	// per-stage counters reset for the same reason — see the Endpoint doc.
	e.recent = nil
	e.stageTotal = 0
	e.stageMatched = 0
}

// Snapshot is a stable, sorted view for reporting.
func (t *Tracker) Snapshot() []*Endpoint {
	t.mu.Lock()
	defer t.mu.Unlock()
	out := make([]*Endpoint, 0, len(t.eps))
	for _, e := range t.eps {
		out = append(out, e)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Route < out[j].Route })
	return out
}

// Report renders the promotion table. It shows the lifetime record and the
// evidence gathered in the current stage side by side, because those are two
// different numbers and confusing them is the failure this package exists to
// prevent.
func (t *Tracker) Report() string {
	s := fmt.Sprintf("%-22s %-8s %9s %9s %10s %11s %12s\n",
		"route", "stage", "requests", "matched", "observed", "in stage", "stage wilson")
	s += fmt.Sprintf("%s\n", stringRepeat("-", 86))
	for _, e := range t.Snapshot() {
		s += fmt.Sprintf("%-22s %-8s %9d %9d %9.2f%% %11d %12.4f\n",
			e.Route, e.stage, e.total, e.matched, e.Rate()*100, e.stageTotal,
			WilsonLower(e.stageMatched, e.stageTotal, t.policy.Z))
	}
	return s
}

func stringRepeat(s string, n int) string {
	out := make([]byte, 0, n*len(s))
	for i := 0; i < n; i++ {
		out = append(out, s...)
	}
	return string(out)
}
