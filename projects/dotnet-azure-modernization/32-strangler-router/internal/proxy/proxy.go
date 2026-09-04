// Package proxy is the shadow reverse proxy.
//
// It sits in front of a legacy service, forwards every request there, and
// returns that response to the client. In parallel it may send a copy of the
// request to a modern implementation and compare the two.
//
// Three decisions dominate the design, and all three are about what the proxy
// refuses to do.
//
// # 1. Mirroring is unsafe by default
//
// The naive shadow proxy mirrors everything. Then the first POST /orders is
// mirrored, the modern implementation writes to a database and calls a payment
// provider, and the customer is charged twice. This proxy mirrors only
// idempotent methods unless a route explicitly opts in via MirrorUnsafe, and
// that opt-in is a per-route decision someone has to write down. See
// docs/adr/0002-unsafe-by-default.md.
//
// # 2. The shadow must never affect the client
//
// The client's response is written from the legacy result and the handler
// returns. The comparison happens on a bounded worker pool afterwards. If the
// modern implementation is slow, hangs, or panics, the client sees nothing: the
// shadow request has its own short timeout, and a full mirror queue drops
// shadow work rather than blocking the request path. Dropped work is counted,
// because a shadow run silently sampling 3% of traffic is worse than one that
// says so.
//
// # 3. Bodies are bounded
//
// Comparing responses requires buffering them. An unbounded buffer in front of
// a streaming endpoint is a memory exhaustion bug with extra steps. Bodies over
// MaxBodyBytes are streamed straight through to the client and excluded from
// comparison, and that exclusion is counted too.
package proxy

import (
	"bytes"
	"context"
	"fmt"
	"io"
	"net/http"
	"sync"
	"sync/atomic"
	"time"

	"strangler/internal/confidence"
	"strangler/internal/jsondiff"
)

// Config configures a Proxy.
type Config struct {
	Legacy  http.Handler
	Modern  http.Handler
	Rules   *jsondiff.Ruleset
	Tracker *confidence.Tracker

	// RouteKey maps a request to the route label used for confidence tracking.
	// Defaults to method + path, which is wrong for anything with an id in the
	// URL — a real deployment supplies its router's pattern.
	RouteKey func(*http.Request) string

	// MirrorUnsafe allows mirroring of non-idempotent methods for routes where
	// someone has confirmed the modern implementation has no side effects.
	MirrorUnsafe func(*http.Request) bool

	// MaxBodyBytes bounds what will be buffered for comparison.
	MaxBodyBytes int64
	// ShadowTimeout bounds the mirrored request.
	ShadowTimeout time.Duration
	// Workers is the size of the comparison pool.
	Workers int
	// QueueDepth is how much shadow work may be pending before it is dropped.
	QueueDepth int

	// OnDivergence is called for every comparison that finds differences.
	OnDivergence func(route string, diffs []jsondiff.Difference, legacy, modern []byte)
}

// Stats are the counters that make a shadow run auditable.
type Stats struct {
	Requests        atomic.Int64
	Mirrored        atomic.Int64
	SkippedUnsafe   atomic.Int64
	SkippedTooLarge atomic.Int64
	DroppedQueue    atomic.Int64
	ShadowErrors    atomic.Int64
	Compared        atomic.Int64
	Diverged        atomic.Int64
}

// Proxy is the shadow router.
type Proxy struct {
	cfg   Config
	Stats Stats

	work chan job
	wg   sync.WaitGroup
	once sync.Once
	stop chan struct{}
}

type job struct {
	route  string
	req    *http.Request
	legacy []byte
	status int
}

// New builds a Proxy and starts its comparison workers.
func New(cfg Config) *Proxy {
	if cfg.RouteKey == nil {
		cfg.RouteKey = func(r *http.Request) string { return r.Method + " " + r.URL.Path }
	}
	if cfg.MirrorUnsafe == nil {
		cfg.MirrorUnsafe = func(*http.Request) bool { return false }
	}
	if cfg.MaxBodyBytes == 0 {
		cfg.MaxBodyBytes = 1 << 20
	}
	if cfg.ShadowTimeout == 0 {
		cfg.ShadowTimeout = 2 * time.Second
	}
	if cfg.Workers == 0 {
		cfg.Workers = 4
	}
	if cfg.QueueDepth == 0 {
		cfg.QueueDepth = 256
	}
	p := &Proxy{cfg: cfg, work: make(chan job, cfg.QueueDepth), stop: make(chan struct{})}
	for i := 0; i < cfg.Workers; i++ {
		p.wg.Add(1)
		go p.worker()
	}
	return p
}

// Close drains the comparison queue. Tests must call it before asserting on
// counters, and a real deployment calls it on shutdown so that in-flight
// comparisons are not lost.
func (p *Proxy) Close() {
	p.once.Do(func() {
		close(p.work)
		p.wg.Wait()
		close(p.stop)
	})
}

func (p *Proxy) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	p.Stats.Requests.Add(1)
	route := p.cfg.RouteKey(r)

	stage := confidence.Shadow
	if p.cfg.Tracker != nil {
		stage = p.cfg.Tracker.Endpoint(route).Stage()
	}

	// At cutover the modern implementation answers and there is nothing to
	// mirror. A halted endpoint is served by legacy and is not mirrored either:
	// it has already diverged, and flooding the report with more of the same
	// divergence buries whatever comes next.
	if stage == confidence.Cutover {
		p.cfg.Modern.ServeHTTP(w, r)
		return
	}

	body, err := p.readBody(r)
	if err != nil {
		http.Error(w, "request body too large", http.StatusRequestEntityTooLarge)
		return
	}

	rec := &recorder{ResponseWriter: w, limit: p.cfg.MaxBodyBytes}
	r.Body = io.NopCloser(bytes.NewReader(body))
	p.cfg.Legacy.ServeHTTP(rec, r)
	rec.flush()

	if stage == confidence.Halted {
		return
	}
	if !p.mirrorable(r) {
		p.Stats.SkippedUnsafe.Add(1)
		return
	}
	if rec.overflowed {
		p.Stats.SkippedTooLarge.Add(1)
		return
	}

	shadow := r.Clone(context.Background())
	shadow.Body = io.NopCloser(bytes.NewReader(body))
	shadow.RequestURI = ""

	select {
	case p.work <- job{route: route, req: shadow, legacy: rec.buf.Bytes(), status: rec.status}:
		p.Stats.Mirrored.Add(1)
	default:
		// The queue is full. Dropping is the only option that keeps the request
		// path honest, and it is counted so the drop rate appears in the report
		// rather than quietly reducing coverage.
		p.Stats.DroppedQueue.Add(1)
	}
}

func (p *Proxy) mirrorable(r *http.Request) bool {
	switch r.Method {
	case http.MethodGet, http.MethodHead, http.MethodOptions:
		return true
	}
	return p.cfg.MirrorUnsafe(r)
}

func (p *Proxy) readBody(r *http.Request) ([]byte, error) {
	if r.Body == nil {
		return nil, nil
	}
	return io.ReadAll(io.LimitReader(r.Body, p.cfg.MaxBodyBytes+1))
}

func (p *Proxy) worker() {
	defer p.wg.Done()
	for j := range p.work {
		p.compare(j)
	}
}

func (p *Proxy) compare(j job) {
	defer func() {
		// A panic in the modern implementation is a finding, not a crash. It is
		// recorded as a divergence so it shows up in the report, and it does not
		// take the proxy down with it.
		if rec := recover(); rec != nil {
			p.Stats.ShadowErrors.Add(1)
			p.Stats.Diverged.Add(1)
			detail := fmt.Sprintf("modern implementation panicked: %v", rec)
			if p.cfg.Tracker != nil {
				p.cfg.Tracker.Record(j.route, false, detail)
			}
			if p.cfg.OnDivergence != nil {
				p.cfg.OnDivergence(j.route, []jsondiff.Difference{{
					Path: "$", Kind: jsondiff.ValueChanged, Note: detail,
				}}, j.legacy, nil)
			}
		}
	}()

	ctx, cancel := context.WithTimeout(context.Background(), p.cfg.ShadowTimeout)
	defer cancel()
	req := j.req.WithContext(ctx)

	rec := &recorder{ResponseWriter: discard{}, limit: p.cfg.MaxBodyBytes}
	p.cfg.Modern.ServeHTTP(rec, req)
	rec.flush()

	p.Stats.Compared.Add(1)

	var diffs []jsondiff.Difference
	if rec.status != j.status {
		diffs = append(diffs, jsondiff.Difference{
			Path: "$status", Kind: jsondiff.ValueChanged,
			Left: j.status, Right: rec.status, Note: "HTTP status"})
	}
	diffs = append(diffs, jsondiff.CompareBytes(j.legacy, rec.buf.Bytes(), p.cfg.Rules)...)

	matched := len(diffs) == 0
	if !matched {
		p.Stats.Diverged.Add(1)
		if p.cfg.OnDivergence != nil {
			p.cfg.OnDivergence(j.route, diffs, j.legacy, rec.buf.Bytes())
		}
	}
	if p.cfg.Tracker != nil {
		detail := ""
		if len(diffs) > 0 {
			detail = diffs[0].String()
		}
		p.cfg.Tracker.Record(j.route, matched, detail)
	}
}

// recorder captures a response while still writing it through.
type recorder struct {
	http.ResponseWriter
	buf        bytes.Buffer
	status     int
	limit      int64
	overflowed bool
	wroteHdr   bool
}

func (r *recorder) WriteHeader(code int) {
	if r.wroteHdr {
		return
	}
	r.status = code
	r.wroteHdr = true
	r.ResponseWriter.WriteHeader(code)
}

func (r *recorder) Write(b []byte) (int, error) {
	if !r.wroteHdr {
		r.WriteHeader(http.StatusOK)
	}
	if int64(r.buf.Len())+int64(len(b)) > r.limit {
		r.overflowed = true
		r.buf.Reset()
	}
	if !r.overflowed {
		r.buf.Write(b)
	}
	return r.ResponseWriter.Write(b)
}

func (r *recorder) flush() {
	if !r.wroteHdr {
		r.status = http.StatusOK
	}
}

// Unwrap lets httputil.ReverseProxy and friends reach the real writer.
func (r *recorder) Unwrap() http.ResponseWriter { return r.ResponseWriter }

type discard struct{}

func (discard) Header() http.Header         { return http.Header{} }
func (discard) Write(b []byte) (int, error) { return len(b), nil }
func (discard) WriteHeader(int)             {}
