// Package backfill models the batched UPDATE loop that fills a new column on
// a very large table, and the controllers that decide how big each batch is.
//
// The constraint is not CPU and it is not lock contention -- batches are
// small enough to take row locks only. It is replication lag. Every batch
// generates WAL; a replica that falls too far behind stops being a usable
// failover target and, on a system that reads from replicas, starts serving
// stale data. The lag budget is a hard operational limit, and the backfill
// has to stay inside it without being told in advance how much WAL the
// replica can absorb.
//
// That is a control problem, and the interesting result is that the obvious
// controller is the wrong one.
package backfill

import "math"

// Replica models lag as a first-order system: lag accumulates in proportion
// to WAL produced and drains at a fixed apply rate.
//
//	lag' = produced/applyRate - 1, floored at zero
//
// The apply rate is unknown to the controller. Discovering it is the whole
// job.
type Replica struct {
	// ApplyRate is rows-worth of WAL the replica can apply per second.
	ApplyRate float64
	// Lag is the current lag in seconds.
	Lag float64
	// Floor is the baseline lag from ordinary traffic.
	Floor float64
}

// Apply advances the replica by dt seconds while `rows` were written.
func (r *Replica) Apply(rows, dt float64) {
	if r.ApplyRate <= 0 {
		return
	}
	r.Lag += rows/r.ApplyRate - dt
	if r.Lag < r.Floor {
		r.Lag = r.Floor
	}
}

// Controller decides the next batch size given the observed lag.
type Controller interface {
	Next(lag float64) int
	Name() string
}

// Fixed is a constant batch size: what almost every hand-written backfill
// script does, with a sleep between batches.
type Fixed struct{ Size int }

func (f Fixed) Next(float64) int { return f.Size }
func (f Fixed) Name() string     { return "fixed" }

// AIMD is additive-increase, multiplicative-decrease -- the TCP congestion
// control rule.
//
// Grow the batch by a constant while lag is under budget; halve it the moment
// lag goes over. It converges on the replica's actual capacity without being
// told what it is, and it is stable because the decrease is aggressive
// relative to the increase.
type AIMD struct {
	Size   float64
	Budget float64
	Inc    float64
	Dec    float64
	Min    float64
	Max    float64
}

func (a *AIMD) Next(lag float64) int {
	if lag > a.Budget {
		a.Size *= a.Dec
	} else {
		a.Size += a.Inc
	}
	a.Size = clamp(a.Size, a.Min, a.Max)
	return int(a.Size)
}
func (a *AIMD) Name() string { return "aimd" }

// AIAD is additive-increase, additive-decrease: the same controller as AIMD
// with the multiplicative decrease replaced by a symmetric additive one.
//
// It exists to isolate which half of AIMD does the work. If stability came
// from "react to lag" then AIAD would be just as stable; if it comes from the
// asymmetry -- creep up, collapse down -- then AIAD will oscillate.
type AIAD struct {
	Size   float64
	Budget float64
	Inc    float64
	Dec    float64
	Min    float64
	Max    float64
}

func (a *AIAD) Next(lag float64) int {
	if lag > a.Budget {
		a.Size -= a.Dec
	} else {
		a.Size += a.Inc
	}
	a.Size = clamp(a.Size, a.Min, a.Max)
	return int(a.Size)
}
func (a *AIAD) Name() string { return "aiad" }

// Proportional is the controller people reach for first: scale the batch size
// by how much lag headroom is left.
//
//	size *= 1 + gain * (budget - lag) / budget
//
// It looks like proportional control and it is not. Multiplying the *size* by
// a term derived from the error makes the error act on the derivative of the
// size, which is integral action, on a plant that already has a transport
// delay -- the lag it reads is the result of batches it issued seconds ago.
// Integral action plus delay is the textbook recipe for a limit cycle, and
// that is exactly what it does.
type Proportional struct {
	Size   float64
	Budget float64
	Gain   float64
	Min    float64
	Max    float64
}

func (p *Proportional) Next(lag float64) int {
	err := (p.Budget - lag) / p.Budget
	p.Size *= 1 + p.Gain*err
	p.Size = clamp(p.Size, p.Min, p.Max)
	return int(p.Size)
}
func (p *Proportional) Name() string { return "proportional" }

func clamp(v, lo, hi float64) float64 { return math.Max(lo, math.Min(hi, v)) }

// Disturbance perturbs the replica mid-run.
type Disturbance struct {
	// At is the second the disturbance starts.
	At float64
	// Until is when it ends.
	Until float64
	// ApplyRateFactor scales the replica's apply rate while active. A
	// concurrent VACUUM, a checkpoint storm, or a second migration on
	// another table all look like this from the backfill's point of view.
	ApplyRateFactor float64
}

// Run is the outcome of a backfill.
type Run struct {
	Controller string
	Rows       int
	Batches    int
	Seconds    float64
	// Violations counts seconds spent over the lag budget.
	Violations int
	// PeakLag is the worst lag observed.
	PeakLag float64
	// Lags is the per-second lag trace.
	Lags []float64
	// Sizes is the per-batch size chosen.
	Sizes []int
	// Completed reports whether the whole table was processed inside the
	// horizon.
	Completed bool
}

// Throughput is rows per second achieved.
func (r Run) Throughput() float64 {
	if r.Seconds == 0 {
		return 0
	}
	return float64(r.Rows) / r.Seconds
}

// Backfill runs a controller against a replica until `total` rows are done or
// `horizon` seconds elapse.
//
// One batch per second: the loop is deliberately coarse, because the decision
// the controller makes is "how much work to do before looking at lag again",
// and a finer clock would model a system that samples lag more often than
// PostgreSQL actually reports it.
func Backfill(c Controller, rep Replica, total int, budget, horizon float64, d *Disturbance) Run {
	r := Run{Controller: c.Name(), Rows: 0}
	base := rep.ApplyRate
	done := 0

	for t := 0.0; t < horizon; t += 1.0 {
		if d != nil {
			if t >= d.At && t < d.Until {
				rep.ApplyRate = base * d.ApplyRateFactor
			} else {
				rep.ApplyRate = base
			}
		}

		size := c.Next(rep.Lag)
		if size < 1 {
			size = 1
		}
		if done+size > total {
			size = total - done
		}
		done += size
		r.Sizes = append(r.Sizes, size)
		r.Batches++

		rep.Apply(float64(size), 1.0)
		r.Lags = append(r.Lags, rep.Lag)
		if rep.Lag > budget {
			r.Violations++
		}
		if rep.Lag > r.PeakLag {
			r.PeakLag = rep.Lag
		}

		r.Seconds = t + 1
		if done >= total {
			r.Completed = true
			break
		}
	}
	r.Rows = done
	return r
}
