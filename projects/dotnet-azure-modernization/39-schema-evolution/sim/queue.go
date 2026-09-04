// Package sim is a deterministic discrete-event model of PostgreSQL's lock
// queue.
//
// It exists to measure one thing: the amplification between how long a DDL
// statement holds a lock and how much query time it costs. Those two numbers
// differ by orders of magnitude, and the reason is queueing, not holding.
//
// PostgreSQL grants table locks in request order. A request that conflicts
// with a current holder waits. Crucially, a request that does *not* conflict
// with any holder but arrives behind a waiter also waits -- otherwise a
// stream of ACCESS SHARE requests would starve an ACCESS EXCLUSIVE request
// forever. That fairness property is correct and it is what converts one
// blocked DDL into a total outage on the table.
package sim

import (
	"fmt"
	"math"
	"sort"

	"evolve/locks"
)

// Request is one lock request in the simulation.
type Request struct {
	ID      int
	Arrive  float64
	Hold    float64 // seconds the lock is held once granted
	Mode    locks.Mode
	Label   string
	Timeout float64 // lock_timeout in seconds; 0 means wait forever
	// Retry is how many times the request re-queues after a timeout.
	Retry int
	// RetryDelay is the pause before re-queueing.
	RetryDelay float64
}

// Result is the outcome of one request.
type Result struct {
	ID        int
	Label     string
	Mode      locks.Mode
	Arrive    float64
	Granted   float64
	Released  float64
	Attempts  int
	TimedOut  bool
	Abandoned bool
}

// Wait is how long the request spent queued across all attempts.
func (r Result) Wait() float64 {
	if r.Abandoned {
		return r.Released - r.Arrive
	}
	return r.Granted - r.Arrive
}

// Outcome is the whole simulation result.
type Outcome struct {
	Results []Result
	// Horizon is the time the last lock was released.
	Horizon float64
	// maxQueue is the longest the wait queue ever became.
	maxQueue int
}

// BlockedSeconds totals queueing time across requests matching pred.
func (o Outcome) BlockedSeconds(pred func(Result) bool) float64 {
	t := 0.0
	for _, r := range o.Results {
		if pred(r) {
			t += r.Wait()
		}
	}
	return t
}

// QuerySeconds totals queueing time for everything that is not the DDL.
func (o Outcome) QuerySeconds(ddlLabel string) float64 {
	return o.BlockedSeconds(func(r Result) bool { return r.Label != ddlLabel })
}

// Abandoned counts requests that exhausted their retries.
func (o Outcome) Abandoned() int {
	n := 0
	for _, r := range o.Results {
		if r.Abandoned {
			n++
		}
	}
	return n
}

// Landed reports whether every request with the given label was granted.
func (o Outcome) Landed(label string) bool {
	found := false
	for _, r := range o.Results {
		if r.Label == label {
			found = true
			if r.Abandoned {
				return false
			}
		}
	}
	return found
}

// MaxQueue is the longest the wait queue ever became.
func (o Outcome) MaxQueue() int { return o.maxQueue }

// event kinds, ordered so that ties break deterministically: releases before
// arrivals before timeouts. A release at time t must be processed before an
// arrival at time t, or the arriving request queues behind a lock that has
// already gone.
type evKind int

const (
	evRelease evKind = iota
	evArrive
	evTimeout
)

type event struct {
	at   float64
	kind evKind
	req  int
	seq  int
}

type heldLock struct {
	req  int
	mode locks.Mode
}

type waiter struct {
	req      int
	since    float64
	attempts int
	deadline float64 // 0 means none
}

// Run executes the simulation.
//
// Deterministic: no randomness, and every tie is broken by a stable rule. The
// same input always produces the same Outcome, which is what lets the report
// be byte-compared between runs.
func Run(reqs []Request) Outcome {
	byID := map[int]Request{}
	for _, r := range reqs {
		if _, dup := byID[r.ID]; dup {
			panic(fmt.Sprintf("sim: duplicate request id %d", r.ID))
		}
		byID[r.ID] = r
	}

	var (
		held     []heldLock
		queue    []waiter
		results  = map[int]*Result{}
		attempts = map[int]int{}
		evs      []event
		seq      int
		horizon  float64
		maxQ     int
	)

	push := func(at float64, k evKind, req int) {
		evs = append(evs, event{at: at, kind: k, req: req, seq: seq})
		seq++
	}

	for _, r := range reqs {
		results[r.ID] = &Result{ID: r.ID, Label: r.Label, Mode: r.Mode, Arrive: r.Arrive}
		push(r.Arrive, evArrive, r.ID)
	}

	pop := func() (event, bool) {
		if len(evs) == 0 {
			return event{}, false
		}
		best := 0
		for i := 1; i < len(evs); i++ {
			a, b := evs[i], evs[best]
			if a.at < b.at-1e-12 ||
				(math.Abs(a.at-b.at) <= 1e-12 && (a.kind < b.kind ||
					(a.kind == b.kind && a.seq < b.seq))) {
				best = i
			}
		}
		e := evs[best]
		evs = append(evs[:best], evs[best+1:]...)
		return e, true
	}

	heldModes := func() []locks.Mode {
		out := make([]locks.Mode, 0, len(held))
		for _, h := range held {
			out = append(out, h.mode)
		}
		return out
	}

	grant := func(w waiter, now float64) {
		r := byID[w.req]
		held = append(held, heldLock{req: w.req, mode: r.Mode})
		res := results[w.req]
		res.Granted = now
		res.Attempts = w.attempts
		push(now+r.Hold, evRelease, w.req)
	}

	// drain grants from the head of the queue for as long as the head is
	// compatible with what is held. It stops at the first incompatible
	// waiter -- that stop is the entire phenomenon this package measures.
	drain := func(now float64) {
		for len(queue) > 0 {
			w := queue[0]
			if !locks.Compatible(heldModes(), byID[w.req].Mode) {
				return
			}
			queue = queue[1:]
			grant(w, now)
		}
	}

	enqueue := func(w waiter, now float64) {
		// A request may only jump the queue if the queue is empty; otherwise
		// it waits behind whoever is already waiting, even if it would not
		// have conflicted.
		if len(queue) == 0 && locks.Compatible(heldModes(), byID[w.req].Mode) {
			grant(w, now)
			return
		}
		queue = append(queue, w)
		if len(queue) > maxQ {
			maxQ = len(queue)
		}
		if d := byID[w.req].Timeout; d > 0 {
			w.deadline = now + d
			queue[len(queue)-1] = w
			push(w.deadline, evTimeout, w.req)
		}
	}

	for {
		e, ok := pop()
		if !ok {
			break
		}
		now := e.at
		if now > horizon {
			horizon = now
		}

		switch e.kind {
		case evArrive:
			attempts[e.req]++
			enqueue(waiter{req: e.req, since: now, attempts: attempts[e.req]}, now)

		case evRelease:
			for i, h := range held {
				if h.req == e.req {
					held = append(held[:i], held[i+1:]...)
					break
				}
			}
			results[e.req].Released = now
			drain(now)

		case evTimeout:
			idx := -1
			for i, w := range queue {
				if w.req == e.req && w.deadline > 0 && math.Abs(w.deadline-now) <= 1e-12 {
					idx = i
					break
				}
			}
			if idx < 0 {
				continue // already granted; the timeout event is stale
			}
			w := queue[idx]
			queue = append(queue[:idx], queue[idx+1:]...)
			r := byID[w.req]
			results[w.req].TimedOut = true
			if w.attempts > r.Retry {
				results[w.req].Abandoned = true
				results[w.req].Released = now
				results[w.req].Attempts = w.attempts
				// Removing a waiter can unblock everything behind it.
				drain(now)
				continue
			}
			// This is the behaviour that makes lock_timeout safe: the DDL
			// gives up, the queue behind it drains, and it tries again later.
			push(now+r.RetryDelay, evArrive, w.req)
			drain(now)
		}
	}

	out := Outcome{Horizon: horizon}
	ids := make([]int, 0, len(results))
	for id := range results {
		ids = append(ids, id)
	}
	sort.Ints(ids)
	for _, id := range ids {
		out.Results = append(out.Results, *results[id])
	}
	out.maxQueue = maxQ
	return out
}
