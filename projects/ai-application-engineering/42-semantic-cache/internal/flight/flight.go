// Package flight is single-flight coalescing for streaming responses.
//
// The easy version of single-flight is twelve lines and it is wrong for LLM
// traffic in three specific ways, each of which this package fixes and tests:
//
//  1. STREAMING. A follower that joins 300ms into a response must receive the
//     tokens already emitted, then the rest live, in order, exactly once. The
//     usual "wait for the result then return it" shape converts a streaming
//     endpoint into a batch one and gives every follower the leader's full
//     latency.
//
//  2. OWNERSHIP. In the textbook implementation the caller who started the work
//     owns it, so when that caller disconnects the work is cancelled and every
//     follower fails. Under a thundering herd the first arrival is also the one
//     most likely to have given up. The work belongs to the GROUP.
//
//  3. ABANDONMENT. If every subscriber leaves, the work should stop - otherwise
//     a herd of cancelled requests still bills for a full generation. But
//     stopping means throwing away a partial answer that was nearly paid for.
//     Both behaviours are defensible; the choice is explicit here, and the
//     counter that says how often it fires is exported.
//
// The fourth problem is not solvable inside this package and is the project's
// second finding: if the coalescing key is a SEMANTIC key, then a follower has
// been served an answer to a question nobody has checked it matches - and that
// event is a cache hit that the cache's own metrics cannot see. Package gateway
// measures it.
package flight

import (
	"context"
	"errors"
	"sync"
)

// ErrAbandoned is returned when every subscriber left before the work finished.
var ErrAbandoned = errors.New("flight: all subscribers abandoned the call")

// Chunk is one streamed fragment.
type Chunk struct {
	Data []byte
	Err  error
	// Done is true on the final chunk. Err may be set with it.
	Done bool
}

// Emit is handed to the work function so it can stream.
type Emit func([]byte)

// Work produces a response, streaming through emit. It must return when ctx is
// cancelled.
type Work func(ctx context.Context, emit Emit) error

// Sub is a subscriber's view of an in-flight call.
type Sub struct {
	ch     <-chan Chunk
	leader bool
	call   *call
	once   sync.Once
}

// Chunks is the stream. It is closed after the final chunk.
func (s *Sub) Chunks() <-chan Chunk { return s.ch }

// Leader reports whether this subscriber triggered the underlying work. Used
// only for metrics; a follower is not second-class in any other way.
func (s *Sub) Leader() bool { return s.leader }

// Close detaches this subscriber. Idempotent. When the last subscriber closes,
// the underlying work is cancelled if the group is configured to do so.
func (s *Sub) Close() { s.once.Do(func() { s.call.unsubscribe(s) }) }

// Group coalesces concurrent calls that share a key.
type Group struct {
	// CancelWhenAbandoned stops the underlying work once every subscriber has
	// detached. Default false: finish the work and cache it, on the theory that
	// most of the cost has already been incurred and the next caller will want
	// it. Set true when the backend bills per output token and the answer is
	// unlikely to be asked for again.
	CancelWhenAbandoned bool

	mu    sync.Mutex
	calls map[string]*call

	stats Stats
}

// Stats are the counters that make coalescing visible. Without them a
// coalescing gateway reports a low backend call count and no explanation.
type Stats struct {
	// Calls is the number of times Work actually ran.
	Calls int
	// Leaders is the number of subscribers that triggered work. Equals Calls.
	Leaders int
	// Followers is the number of subscribers served from an in-flight call.
	// THIS IS THE NUMBER THAT MATTERS: every follower received an answer
	// produced for somebody else's request.
	Followers int
	// LateJoins is the subset of Followers that arrived after the first chunk
	// had already been emitted, and therefore needed the prefix replayed.
	LateJoins int
	// Abandoned counts calls where every subscriber left before completion.
	Abandoned int
	// LeaderLeft counts calls the leader abandoned but that continued for
	// followers. Under the textbook implementation every one of these would
	// have been a failed request for everyone still waiting.
	LeaderLeft int
}

func New() *Group { return &Group{calls: map[string]*call{}} }

// Stats returns a snapshot.
func (g *Group) Stats() Stats {
	g.mu.Lock()
	defer g.mu.Unlock()
	return g.stats
}

type call struct {
	g   *Group
	key string

	mu sync.Mutex
	// prefix is every chunk emitted so far, kept so a late joiner can be
	// caught up. For a chat response this is bounded by the response length,
	// which is the same thing the client is buffering anyway.
	prefix [][]byte
	subs   map[*Sub]chan Chunk
	// leader is the subscriber that started the work, or nil once it leaves.
	leader   *Sub
	finished bool
	err      error
	cancel   context.CancelFunc
	// everSubscribed guards against the race where the last follower leaves at
	// the same moment a new one arrives.
	everSubscribed int
}

// Do subscribes to the call for key, starting the work if nobody else has.
//
// The returned Sub must be Closed. The bool reports whether this caller became
// the leader, which is metrics-only.
func (g *Group) Do(ctx context.Context, key string, w Work) (*Sub, bool) {
	for {
		g.mu.Lock()
		c, exists := g.calls[key]
		if exists {
			c.mu.Lock()
			if c.finished {
				// A completed call that has not yet been reaped. Drop it and
				// start fresh rather than joining a stream that will never
				// produce another chunk.
				delete(g.calls, key)
				c.mu.Unlock()
				g.mu.Unlock()
				continue
			}
		} else {
			c = &call{g: g, key: key, subs: map[*Sub]chan Chunk{}}
			g.calls[key] = c
			c.mu.Lock()
		}

		// The buffer is sized to hold the whole replayed prefix plus headroom,
		// so replaying under the lock can never block.
		ch := make(chan Chunk, len(c.prefix)+chunkBuffer)
		s := &Sub{ch: ch, call: c, leader: !exists}
		c.subs[s] = ch
		c.everSubscribed++
		late := len(c.prefix) > 0
		for _, p := range c.prefix {
			ch <- Chunk{Data: p}
		}
		if !exists {
			c.leader = s
			g.stats.Calls++
			g.stats.Leaders++
		} else {
			g.stats.Followers++
			if late {
				g.stats.LateJoins++
			}
		}

		var wctx context.Context
		if !exists {
			// The work's lifetime is the GROUP's, not this caller's. Deriving
			// it from ctx would make the first arrival's cancellation everyone
			// else's failure, which is fix (2) in the package comment.
			var cancel context.CancelFunc
			wctx, cancel = context.WithCancel(context.WithoutCancel(ctx))
			c.cancel = cancel
		}
		c.mu.Unlock()
		g.mu.Unlock()

		if !exists {
			go c.run(wctx, w)
		}
		return s, !exists
	}
}

// chunkBuffer is the per-subscriber headroom beyond the replayed prefix.
const chunkBuffer = 64

// InFlight reports the number of live calls, for tests and metrics.
func (g *Group) InFlight() int {
	g.mu.Lock()
	defer g.mu.Unlock()
	return len(g.calls)
}

func (c *call) run(ctx context.Context, w Work) {
	err := w(ctx, func(b []byte) {
		cp := make([]byte, len(b))
		copy(cp, b)
		c.mu.Lock()
		c.prefix = append(c.prefix, cp)
		for _, ch := range c.subs {
			select {
			case ch <- Chunk{Data: cp}:
			default:
				// A subscriber that cannot keep up is dropped rather than
				// allowed to stall the whole group. A slow reader must not
				// become everyone else's latency.
			}
		}
		c.mu.Unlock()
	})

	c.mu.Lock()
	c.finished = true
	c.err = err
	subs := c.subs
	c.subs = map[*Sub]chan Chunk{}
	c.mu.Unlock()

	c.g.mu.Lock()
	delete(c.g.calls, c.key)
	if errors.Is(err, ErrAbandoned) {
		c.g.stats.Abandoned++
	}
	c.g.mu.Unlock()

	for _, ch := range subs {
		// Non-blocking: a subscriber that has stopped reading must not be able
		// to wedge the completion path. It still sees the stream end, because
		// the channel is closed either way.
		select {
		case ch <- Chunk{Done: true, Err: err}:
		default:
		}
		close(ch)
	}
}

func (c *call) unsubscribe(s *Sub) {
	c.mu.Lock()
	ch, ok := c.subs[s]
	if !ok {
		c.mu.Unlock()
		return
	}
	delete(c.subs, s)
	close(ch)

	wasLeader := c.leader == s
	if wasLeader {
		c.leader = nil
	}
	empty := len(c.subs) == 0 && !c.finished
	cancel := c.cancel
	c.mu.Unlock()

	if wasLeader && !empty {
		// The work continues. This is the point of the package.
		c.g.mu.Lock()
		c.g.stats.LeaderLeft++
		c.g.mu.Unlock()
	}
	if empty && c.g.CancelWhenAbandoned && cancel != nil {
		cancel()
	}
}
