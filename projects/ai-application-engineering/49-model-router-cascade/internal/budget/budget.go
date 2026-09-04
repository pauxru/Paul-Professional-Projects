// Package budget is the part that stops a router from becoming an incident.
//
// A cost-optimising router has a failure mode nobody designs for: it works.
// Traffic shifts to the model that answers well, that provider degrades, and
// every request now retries into a timeout on a shared connection pool. The
// routing policy is correct the whole time and the service is down.
//
// So: per-tenant spend caps that degrade rather than fail, and a circuit
// breaker per provider with a half-open probe so recovery is measured rather
// than assumed.
package budget

import (
	"errors"
	"fmt"
	"sort"
	"sync"
	"time"
)

// ErrExhausted means the tenant is out of budget and no cheaper model exists.
var ErrExhausted = errors.New("budget exhausted")

// ErrOpen means the provider's breaker is open.
var ErrOpen = errors.New("circuit open")

// Ledger tracks per-tenant spend against a cap.
//
// Spend is in tenths of a cent as an integer. Floating point money in a
// metering path is how you end up with a reconciliation ticket: 0.1+0.2 is
// not 0.3, and after ten million requests the drift is real money.
type Ledger struct {
	mu    sync.Mutex
	caps  map[string]int64
	spent map[string]int64
	// Degraded counts requests served by a cheaper model because the tenant
	// was near its cap. This is the number to alert on: it means a customer
	// is silently getting a worse product.
	degraded map[string]int
	denied   map[string]int
}

func NewLedger() *Ledger {
	return &Ledger{
		caps:     map[string]int64{},
		spent:    map[string]int64{},
		degraded: map[string]int{},
		denied:   map[string]int{},
	}
}

// SetCap sets a tenant's cap in tenths of a cent.
func (l *Ledger) SetCap(tenant string, cap int64) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.caps[tenant] = cap
}

// Remaining is the tenant's headroom.
func (l *Ledger) Remaining(tenant string) int64 {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.caps[tenant] - l.spent[tenant]
}

// Reserve takes budget for a call that is about to happen. It is a reservation
// rather than a post-hoc charge because the alternative is discovering the
// overspend after the provider has already been billed.
func (l *Ledger) Reserve(tenant string, cost int64) bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.spent[tenant]+cost > l.caps[tenant] {
		return false
	}
	l.spent[tenant] += cost
	return true
}

// Refund returns unused reservation, e.g. when a cascade did not escalate.
func (l *Ledger) Refund(tenant string, cost int64) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.spent[tenant] -= cost
	if l.spent[tenant] < 0 {
		l.spent[tenant] = 0
	}
}

func (l *Ledger) MarkDegraded(tenant string) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.degraded[tenant]++
}

func (l *Ledger) MarkDenied(tenant string) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.denied[tenant]++
}

type TenantStat struct {
	Tenant   string
	Cap      int64
	Spent    int64
	Degraded int
	Denied   int
}

func (l *Ledger) Stats() []TenantStat {
	l.mu.Lock()
	defer l.mu.Unlock()
	var out []TenantStat
	for t, c := range l.caps {
		out = append(out, TenantStat{t, c, l.spent[t], l.degraded[t], l.denied[t]})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Tenant < out[j].Tenant })
	return out
}

// State is a breaker state.
type State int

const (
	Closed State = iota
	Open
	HalfOpen
)

func (s State) String() string {
	switch s {
	case Closed:
		return "closed"
	case Open:
		return "open"
	default:
		return "half-open"
	}
}

// Breaker is a per-provider circuit breaker.
//
// The half-open state exists because a breaker that slams straight back to
// closed after a cooldown will re-open on the first burst it lets through, and
// you get a service that oscillates instead of recovering. Half-open admits a
// fixed number of probes and requires all of them to succeed.
type Breaker struct {
	Name string
	// Threshold is consecutive failures before opening.
	Threshold int
	// Cooldown is how long the breaker stays open before probing.
	Cooldown time.Duration
	// Probes is how many consecutive successes half-open requires.
	Probes int

	mu          sync.Mutex
	state       State
	failures    int
	successes   int
	openedAt    time.Time
	transitions []Transition
}

// Transition records a state change, so a test can assert the sequence rather
// than the final state. Most breaker bugs are in the path, not the destination.
type Transition struct {
	At   time.Time
	From State
	To   State
	Why  string
}

func NewBreaker(name string, threshold int, cooldown time.Duration, probes int) *Breaker {
	return &Breaker{Name: name, Threshold: threshold, Cooldown: cooldown, Probes: probes}
}

// Allow reports whether a call may proceed at time now.
func (b *Breaker) Allow(now time.Time) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.state == Open && now.Sub(b.openedAt) >= b.Cooldown {
		b.to(now, HalfOpen, "cooldown elapsed")
		b.successes = 0
	}
	return b.state != Open
}

func (b *Breaker) Success(now time.Time) {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.failures = 0
	if b.state == HalfOpen {
		b.successes++
		if b.successes >= b.Probes {
			b.to(now, Closed, fmt.Sprintf("%d probes succeeded", b.Probes))
		}
	}
}

func (b *Breaker) Failure(now time.Time) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.state == HalfOpen {
		// One failed probe is enough. The provider is not well.
		b.openedAt = now
		b.to(now, Open, "probe failed")
		b.failures = b.Threshold
		return
	}
	b.failures++
	if b.state == Closed && b.failures >= b.Threshold {
		b.openedAt = now
		b.to(now, Open, fmt.Sprintf("%d consecutive failures", b.failures))
	}
}

func (b *Breaker) to(now time.Time, s State, why string) {
	if b.state == s {
		return
	}
	b.transitions = append(b.transitions, Transition{now, b.state, s, why})
	b.state = s
}

func (b *Breaker) State() State {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.state
}

func (b *Breaker) Transitions() []Transition {
	b.mu.Lock()
	defer b.mu.Unlock()
	out := make([]Transition, len(b.transitions))
	copy(out, b.transitions)
	return out
}

// Fleet routes around open breakers.
type Fleet struct {
	breakers map[string]*Breaker
	order    []string
}

func NewFleet() *Fleet { return &Fleet{breakers: map[string]*Breaker{}} }

func (f *Fleet) Add(b *Breaker) {
	f.breakers[b.Name] = b
	f.order = append(f.order, b.Name)
}

func (f *Fleet) Get(name string) *Breaker { return f.breakers[name] }

// Pick returns the first allowed provider from prefs, or "" if all are open.
// Preference order is the caller's, so a router can express "the model I want,
// then the one I'll settle for" without this package knowing about models.
func (f *Fleet) Pick(now time.Time, prefs []string) string {
	for _, p := range prefs {
		b, ok := f.breakers[p]
		if !ok {
			return p
		}
		if b.Allow(now) {
			return p
		}
	}
	return ""
}
