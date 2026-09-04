package budget

import (
	"testing"
	"time"
)

func TestReserveRespectsTheCap(t *testing.T) {
	l := NewLedger()
	l.SetCap("acme", 100)
	if !l.Reserve("acme", 60) {
		t.Fatal("first reservation should succeed")
	}
	if !l.Reserve("acme", 40) {
		t.Fatal("a reservation that exactly exhausts the cap should succeed")
	}
	if l.Reserve("acme", 1) {
		t.Fatal("a reservation past the cap should be refused")
	}
	if got := l.Remaining("acme"); got != 0 {
		t.Errorf("remaining %d, want 0", got)
	}
}

func TestUnknownTenantHasNoBudget(t *testing.T) {
	l := NewLedger()
	if l.Reserve("stranger", 1) {
		t.Fatal("a tenant with no cap should not be able to spend")
	}
}

func TestRefundReturnsHeadroom(t *testing.T) {
	l := NewLedger()
	l.SetCap("acme", 100)
	l.Reserve("acme", 100)
	l.Refund("acme", 40)
	if got := l.Remaining("acme"); got != 40 {
		t.Errorf("remaining %d, want 40", got)
	}
	if !l.Reserve("acme", 40) {
		t.Fatal("refunded budget should be spendable again")
	}
}

func TestRefundNeverDrivesSpendNegative(t *testing.T) {
	l := NewLedger()
	l.SetCap("acme", 100)
	l.Reserve("acme", 10)
	l.Refund("acme", 999)
	if got := l.Remaining("acme"); got != 100 {
		t.Errorf("remaining %d, want the full cap", got)
	}
}

func TestTenantsAreIsolated(t *testing.T) {
	l := NewLedger()
	l.SetCap("a", 100)
	l.SetCap("b", 100)
	for i := 0; i < 10; i++ {
		l.Reserve("a", 10)
	}
	if l.Remaining("a") != 0 {
		t.Fatal("a should be exhausted")
	}
	if l.Remaining("b") != 100 {
		t.Fatal("b's budget was consumed by a")
	}
}

func TestStatsAreSortedAndComplete(t *testing.T) {
	l := NewLedger()
	for _, n := range []string{"zeta", "alpha", "mid"} {
		l.SetCap(n, 50)
	}
	l.Reserve("alpha", 20)
	l.MarkDegraded("alpha")
	l.MarkDenied("zeta")
	s := l.Stats()
	if len(s) != 3 {
		t.Fatalf("expected 3 tenants, got %d", len(s))
	}
	if s[0].Tenant != "alpha" || s[1].Tenant != "mid" || s[2].Tenant != "zeta" {
		t.Fatalf("stats are not sorted: %+v", s)
	}
	if s[0].Spent != 20 || s[0].Degraded != 1 {
		t.Errorf("alpha stats wrong: %+v", s[0])
	}
	if s[2].Denied != 1 {
		t.Errorf("zeta stats wrong: %+v", s[2])
	}
}

// Money is tracked in integers on purpose. Ten million requests of 0.1 cents
// must come to exactly one million cents, not 999,999.99999.
func TestSpendDoesNotDrift(t *testing.T) {
	l := NewLedger()
	const n = 1000000
	l.SetCap("acme", n)
	for i := 0; i < n; i++ {
		if !l.Reserve("acme", 1) {
			t.Fatalf("reservation %d refused", i)
		}
	}
	if got := l.Stats()[0].Spent; got != n {
		t.Fatalf("spent %d after %d reservations of 1", got, n)
	}
	if l.Reserve("acme", 1) {
		t.Fatal("the cap was not exact")
	}
}

func newBreaker() *Breaker { return NewBreaker("p", 3, 30*time.Second, 2) }

func TestBreakerOpensOnConsecutiveFailures(t *testing.T) {
	b := newBreaker()
	now := time.Unix(0, 0)
	for i := 0; i < 2; i++ {
		b.Failure(now)
		if b.State() != Closed {
			t.Fatalf("opened after only %d failures", i+1)
		}
	}
	b.Failure(now)
	if b.State() != Open {
		t.Fatal("did not open at the threshold")
	}
	if b.Allow(now) {
		t.Fatal("an open breaker should not admit traffic")
	}
}

func TestSuccessResetsTheFailureRun(t *testing.T) {
	b := newBreaker()
	now := time.Unix(0, 0)
	b.Failure(now)
	b.Failure(now)
	b.Success(now)
	b.Failure(now)
	b.Failure(now)
	if b.State() != Closed {
		t.Fatal("failures either side of a success were treated as consecutive")
	}
}

func TestBreakerHalfOpensAfterCooldown(t *testing.T) {
	b := newBreaker()
	now := time.Unix(0, 0)
	for i := 0; i < 3; i++ {
		b.Failure(now)
	}
	if b.Allow(now.Add(29 * time.Second)) {
		t.Fatal("probed before the cooldown elapsed")
	}
	if !b.Allow(now.Add(30 * time.Second)) {
		t.Fatal("did not probe after the cooldown")
	}
	if b.State() != HalfOpen {
		t.Fatalf("state is %v, want half-open", b.State())
	}
}

// The false dawn. A single success must not restore full traffic.
func TestHalfOpenRequiresEveryProbe(t *testing.T) {
	b := newBreaker()
	now := time.Unix(0, 0)
	for i := 0; i < 3; i++ {
		b.Failure(now)
	}
	now = now.Add(30 * time.Second)
	b.Allow(now)
	b.Success(now)
	if b.State() != HalfOpen {
		t.Fatal("closed after a single probe; the whole point of half-open is lost")
	}
	b.Success(now)
	if b.State() != Closed {
		t.Fatal("did not close after the required number of probes")
	}
}

func TestOneFailedProbeReopensImmediately(t *testing.T) {
	b := newBreaker()
	now := time.Unix(0, 0)
	for i := 0; i < 3; i++ {
		b.Failure(now)
	}
	now = now.Add(30 * time.Second)
	b.Allow(now)
	b.Success(now)
	b.Failure(now)
	if b.State() != Open {
		t.Fatalf("state is %v after a failed probe, want open", b.State())
	}
	// And it must wait a full cooldown again, measured from the failed probe.
	if b.Allow(now.Add(29 * time.Second)) {
		t.Fatal("the cooldown did not restart from the failed probe")
	}
	if !b.Allow(now.Add(30 * time.Second)) {
		t.Fatal("never probed again")
	}
}

// Most breaker bugs are in the path, not the destination, so assert the path.
func TestTransitionSequenceIsRecorded(t *testing.T) {
	b := newBreaker()
	now := time.Unix(0, 0)
	for i := 0; i < 3; i++ {
		b.Failure(now)
	}
	now = now.Add(30 * time.Second)
	b.Allow(now)
	b.Failure(now)
	now = now.Add(30 * time.Second)
	b.Allow(now)
	b.Success(now)
	b.Success(now)

	want := []struct{ from, to State }{
		{Closed, Open},
		{Open, HalfOpen},
		{HalfOpen, Open},
		{Open, HalfOpen},
		{HalfOpen, Closed},
	}
	got := b.Transitions()
	if len(got) != len(want) {
		t.Fatalf("got %d transitions, want %d: %+v", len(got), len(want), got)
	}
	for i := range want {
		if got[i].From != want[i].from || got[i].To != want[i].to {
			t.Errorf("transition %d: %v->%v, want %v->%v",
				i, got[i].From, got[i].To, want[i].from, want[i].to)
		}
		if got[i].Why == "" {
			t.Errorf("transition %d has no reason recorded", i)
		}
	}
}

func TestTransitionsAreACopy(t *testing.T) {
	b := newBreaker()
	now := time.Unix(0, 0)
	for i := 0; i < 3; i++ {
		b.Failure(now)
	}
	ts := b.Transitions()
	ts[0].Why = "tampered"
	if b.Transitions()[0].Why == "tampered" {
		t.Fatal("Transitions handed out its internal slice")
	}
}

func TestNoDuplicateTransitionsForRepeatedFailures(t *testing.T) {
	b := newBreaker()
	now := time.Unix(0, 0)
	for i := 0; i < 20; i++ {
		b.Failure(now)
	}
	if n := len(b.Transitions()); n != 1 {
		t.Fatalf("%d transitions recorded for one outage, want 1", n)
	}
}

func TestFleetPrefersTheFirstAllowedProvider(t *testing.T) {
	f := NewFleet()
	a := NewBreaker("a", 2, time.Minute, 1)
	bb := NewBreaker("b", 2, time.Minute, 1)
	f.Add(a)
	f.Add(bb)
	now := time.Unix(0, 0)

	if got := f.Pick(now, []string{"a", "b"}); got != "a" {
		t.Fatalf("picked %q, want a", got)
	}
	a.Failure(now)
	a.Failure(now)
	if got := f.Pick(now, []string{"a", "b"}); got != "b" {
		t.Fatalf("picked %q after a opened, want b", got)
	}
	bb.Failure(now)
	bb.Failure(now)
	if got := f.Pick(now, []string{"a", "b"}); got != "" {
		t.Fatalf("picked %q with everything open, want the empty string", got)
	}
}

func TestFleetPassesThroughUnknownProviders(t *testing.T) {
	f := NewFleet()
	if got := f.Pick(time.Unix(0, 0), []string{"unmonitored"}); got != "unmonitored" {
		t.Fatalf("picked %q, want the unmonitored provider", got)
	}
}

func TestStateStringsAreStable(t *testing.T) {
	for s, want := range map[State]string{Closed: "closed", Open: "open", HalfOpen: "half-open"} {
		if s.String() != want {
			t.Errorf("%d.String() = %q, want %q", s, s.String(), want)
		}
	}
}
