package flight

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

// drain reads a subscriber to completion and returns the concatenated body.
func drain(s *Sub) ([]byte, error) {
	var buf bytes.Buffer
	for c := range s.Chunks() {
		if c.Done {
			return buf.Bytes(), c.Err
		}
		buf.Write(c.Data)
	}
	return buf.Bytes(), nil
}

func emitAll(parts ...string) Work {
	return func(ctx context.Context, emit Emit) error {
		for _, p := range parts {
			select {
			case <-ctx.Done():
				return ctx.Err()
			default:
			}
			emit([]byte(p))
		}
		return nil
	}
}

func TestASingleCallerGetsTheWholeStream(t *testing.T) {
	g := New()
	s, leader := g.Do(context.Background(), "k", emitAll("a", "b", "c"))
	if !leader {
		t.Error("the first caller must be the leader")
	}
	defer s.Close()
	body, err := drain(s)
	if err != nil {
		t.Fatal(err)
	}
	if string(body) != "abc" {
		t.Errorf("body %q, want abc", body)
	}
}

// The core promise. A herd of identical concurrent requests costs one call.
func TestAHerdCostsOneCall(t *testing.T) {
	g := New()
	release := make(chan struct{})
	var runs int32

	work := func(ctx context.Context, emit Emit) error {
		atomic.AddInt32(&runs, 1)
		<-release
		emit([]byte("hello"))
		return nil
	}

	const herd = 64
	var wg sync.WaitGroup
	bodies := make([][]byte, herd)
	subs := make([]*Sub, herd)
	for i := 0; i < herd; i++ {
		subs[i], _ = g.Do(context.Background(), "k", work)
	}
	for i := 0; i < herd; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			defer subs[i].Close()
			bodies[i], _ = drain(subs[i])
		}(i)
	}
	close(release)
	wg.Wait()

	if got := atomic.LoadInt32(&runs); got != 1 {
		t.Fatalf("work ran %d times for %d callers, want 1", got, herd)
	}
	for i, b := range bodies {
		if string(b) != "hello" {
			t.Fatalf("subscriber %d got %q, want hello", i, b)
		}
	}
	st := g.Stats()
	if st.Calls != 1 || st.Leaders != 1 || st.Followers != herd-1 {
		t.Errorf("stats %+v, want 1 call, 1 leader, %d followers", st, herd-1)
	}
}

// A subscriber that arrives mid-stream must receive the tokens already emitted,
// then the rest live, in order. Without prefix replay it would silently get a
// truncated answer, which is worse than an error.
func TestALateJoinerGetsTheWholeStreamFromTheBeginning(t *testing.T) {
	g := New()
	first := make(chan struct{})
	rest := make(chan struct{})

	work := func(ctx context.Context, emit Emit) error {
		emit([]byte("one "))
		emit([]byte("two "))
		close(first)
		<-rest
		emit([]byte("three"))
		return nil
	}

	leaderSub, _ := g.Do(context.Background(), "k", work)
	<-first

	lateSub, isLeader := g.Do(context.Background(), "k", work)
	if isLeader {
		t.Fatal("the late joiner must not become a leader")
	}
	close(rest)

	var wg sync.WaitGroup
	var lateBody, leadBody []byte
	wg.Add(2)
	go func() { defer wg.Done(); defer lateSub.Close(); lateBody, _ = drain(lateSub) }()
	go func() { defer wg.Done(); defer leaderSub.Close(); leadBody, _ = drain(leaderSub) }()
	wg.Wait()

	const want = "one two three"
	if string(lateBody) != want {
		t.Errorf("late joiner got %q, want %q", lateBody, want)
	}
	if string(leadBody) != want {
		t.Errorf("leader got %q, want %q", leadBody, want)
	}
	if st := g.Stats(); st.LateJoins != 1 {
		t.Errorf("LateJoins = %d, want 1", st.LateJoins)
	}
}

// Fix (2). In the textbook single-flight the leader owns the work, so when the
// first arrival gives up everybody waiting fails. Under a thundering herd the
// first arrival is exactly the caller who has been waiting longest and is most
// likely to have given up.
func TestTheWorkSurvivesTheLeaderLeaving(t *testing.T) {
	g := New()
	started := make(chan struct{})
	finish := make(chan struct{})

	work := func(ctx context.Context, emit Emit) error {
		close(started)
		<-finish
		select {
		case <-ctx.Done():
			return ctx.Err()
		default:
		}
		emit([]byte("survived"))
		return nil
	}

	leaderSub, _ := g.Do(context.Background(), "k", work)
	<-started
	followerSub, _ := g.Do(context.Background(), "k", work)

	leaderSub.Close() // the leader walks away
	close(finish)

	body, err := drain(followerSub)
	followerSub.Close()
	if err != nil {
		t.Fatalf("follower failed after the leader left: %v", err)
	}
	if string(body) != "survived" {
		t.Errorf("follower got %q, want survived", body)
	}
	if st := g.Stats(); st.LeaderLeft != 1 {
		t.Errorf("LeaderLeft = %d, want 1", st.LeaderLeft)
	}
}

// The leader's context being cancelled is the same situation as the leader
// closing, and must likewise not take the group down.
func TestCancellingTheLeadersContextDoesNotKillTheGroup(t *testing.T) {
	g := New()
	started := make(chan struct{})
	finish := make(chan struct{})
	work := func(ctx context.Context, emit Emit) error {
		close(started)
		<-finish
		if ctx.Err() != nil {
			return ctx.Err()
		}
		emit([]byte("ok"))
		return nil
	}

	lctx, lcancel := context.WithCancel(context.Background())
	leaderSub, _ := g.Do(lctx, "k", work)
	<-started
	followerSub, _ := g.Do(context.Background(), "k", work)

	lcancel()
	time.Sleep(5 * time.Millisecond)
	close(finish)

	body, err := drain(followerSub)
	followerSub.Close()
	leaderSub.Close()
	if err != nil {
		t.Fatalf("follower failed when the leader's context was cancelled: %v", err)
	}
	if string(body) != "ok" {
		t.Errorf("follower got %q", body)
	}
}

// Fix (3), the opt-in half: when nobody is listening any more, stop paying.
func TestAbandonedWorkIsCancelledWhenConfigured(t *testing.T) {
	g := New()
	g.CancelWhenAbandoned = true
	started := make(chan struct{})
	cancelled := make(chan struct{})

	work := func(ctx context.Context, emit Emit) error {
		close(started)
		<-ctx.Done()
		close(cancelled)
		return ctx.Err()
	}

	s, _ := g.Do(context.Background(), "k", work)
	<-started
	s.Close()

	select {
	case <-cancelled:
	case <-time.After(2 * time.Second):
		t.Fatal("work was not cancelled after the last subscriber left")
	}
}

// And the default half: finishing is also defensible, because most of the cost
// has been paid and the answer is worth caching. The choice must be explicit,
// so pin the default.
func TestAbandonedWorkFinishesByDefault(t *testing.T) {
	g := New()
	started := make(chan struct{})
	done := make(chan struct{})

	work := func(ctx context.Context, emit Emit) error {
		close(started)
		time.Sleep(10 * time.Millisecond)
		if ctx.Err() != nil {
			t.Error("work was cancelled although CancelWhenAbandoned is false")
		}
		emit([]byte("finished anyway"))
		close(done)
		return nil
	}

	s, _ := g.Do(context.Background(), "k", work)
	<-started
	s.Close()

	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("work never completed")
	}
}

func TestErrorsFanOutToEverySubscriber(t *testing.T) {
	g := New()
	boom := errors.New("backend exploded")
	release := make(chan struct{})
	work := func(ctx context.Context, emit Emit) error {
		<-release
		emit([]byte("partial"))
		return boom
	}

	const n = 8
	subs := make([]*Sub, n)
	for i := range subs {
		subs[i], _ = g.Do(context.Background(), "k", work)
	}
	close(release)

	for i, s := range subs {
		body, err := drain(s)
		s.Close()
		if !errors.Is(err, boom) {
			t.Errorf("subscriber %d got err %v, want %v", i, err, boom)
		}
		if string(body) != "partial" {
			t.Errorf("subscriber %d got %q, want the partial output", i, body)
		}
	}
}

// Different keys must not coalesce. Obvious, and the first thing a hash bug
// breaks.
func TestDifferentKeysDoNotCoalesce(t *testing.T) {
	g := New()
	var runs int32
	work := func(ctx context.Context, emit Emit) error {
		atomic.AddInt32(&runs, 1)
		emit([]byte("x"))
		return nil
	}
	for i := 0; i < 10; i++ {
		s, _ := g.Do(context.Background(), fmt.Sprintf("k%d", i), work)
		drain(s)
		s.Close()
	}
	if got := atomic.LoadInt32(&runs); got != 10 {
		t.Errorf("work ran %d times for 10 distinct keys", got)
	}
}

// A completed call must be reaped, so the NEXT request for the same key runs
// fresh work rather than subscribing to a stream that will never emit again.
func TestASecondRequestAfterCompletionRunsAgain(t *testing.T) {
	g := New()
	var runs int32
	work := func(ctx context.Context, emit Emit) error {
		atomic.AddInt32(&runs, 1)
		emit([]byte("v"))
		return nil
	}
	for i := 0; i < 3; i++ {
		s, leader := g.Do(context.Background(), "k", work)
		if !leader {
			t.Fatalf("call %d joined a finished flight instead of starting a new one", i)
		}
		body, err := drain(s)
		s.Close()
		if err != nil || string(body) != "v" {
			t.Fatalf("call %d: body %q err %v", i, body, err)
		}
	}
	if got := atomic.LoadInt32(&runs); got != 3 {
		t.Errorf("work ran %d times for 3 sequential requests, want 3", got)
	}
	if g.InFlight() != 0 {
		t.Errorf("%d calls still in flight after everything completed", g.InFlight())
	}
}

func TestCloseIsIdempotent(t *testing.T) {
	g := New()
	s, _ := g.Do(context.Background(), "k", emitAll("a"))
	drain(s)
	s.Close()
	s.Close()
	s.Close()
}

func TestInFlightReturnsToZero(t *testing.T) {
	g := New()
	var wg sync.WaitGroup
	for i := 0; i < 20; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			s, _ := g.Do(context.Background(), fmt.Sprintf("k%d", i%4), emitAll("a", "b"))
			drain(s)
			s.Close()
		}(i)
	}
	wg.Wait()
	deadline := time.Now().Add(2 * time.Second)
	for g.InFlight() != 0 && time.Now().Before(deadline) {
		time.Sleep(time.Millisecond)
	}
	if g.InFlight() != 0 {
		t.Errorf("%d calls still in flight", g.InFlight())
	}
}

// The stress test that justifies -race in test.ps1. Everything happens at once:
// herds on shared keys, late joiners, leaders abandoning, and repeat requests
// after completion.
func TestConcurrentChaos(t *testing.T) {
	g := New()
	const workers = 200
	var wg sync.WaitGroup
	for i := 0; i < workers; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			key := fmt.Sprintf("k%d", i%5)
			s, leader := g.Do(context.Background(), key, func(ctx context.Context, emit Emit) error {
				for j := 0; j < 5; j++ {
					emit([]byte{byte('a' + j)})
				}
				return nil
			})
			// A third of the leaders walk away mid-stream.
			if leader && i%3 == 0 {
				s.Close()
				return
			}
			body, _ := drain(s)
			s.Close()
			if len(body) != 0 && string(body) != "abcde" {
				t.Errorf("worker %d got a torn body %q", i, body)
			}
		}(i)
	}
	wg.Wait()

	st := g.Stats()
	if st.Calls != st.Leaders {
		t.Errorf("Calls %d != Leaders %d", st.Calls, st.Leaders)
	}
	if st.Calls+st.Followers < workers {
		t.Errorf("only %d of %d requests were accounted for", st.Calls+st.Followers, workers)
	}
}

// Every request must be either a leader or a follower. If this identity ever
// fails, the coalescing rate being reported is fiction.
func TestEveryRequestIsALeaderOrAFollower(t *testing.T) {
	g := New()
	release := make(chan struct{})
	const n = 50
	subs := make([]*Sub, n)
	for i := range subs {
		subs[i], _ = g.Do(context.Background(), "same", func(ctx context.Context, emit Emit) error {
			<-release
			emit([]byte("x"))
			return nil
		})
	}
	close(release)
	for _, s := range subs {
		drain(s)
		s.Close()
	}
	st := g.Stats()
	if st.Leaders+st.Followers != n {
		t.Errorf("leaders %d + followers %d != %d requests", st.Leaders, st.Followers, n)
	}
}
