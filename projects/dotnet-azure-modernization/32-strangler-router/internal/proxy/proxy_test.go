package proxy

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"strangler/internal/confidence"
	"strangler/internal/jsondiff"
)

func jsonHandler(body string) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.Write([]byte(body))
	})
}

func newTestProxy(t *testing.T, legacy, modern http.Handler, tweak func(*Config)) *Proxy {
	t.Helper()
	rs, err := jsondiff.Compile(nil)
	if err != nil {
		t.Fatal(err)
	}
	cfg := Config{
		Legacy:   legacy,
		Modern:   modern,
		Rules:    rs,
		RouteKey: func(r *http.Request) string { return r.Method + " " + r.URL.Path },
	}
	if tweak != nil {
		tweak(&cfg)
	}
	return New(cfg)
}

func get(p *Proxy, path string) *httptest.ResponseRecorder {
	req := httptest.NewRequest(http.MethodGet, path, nil)
	rec := httptest.NewRecorder()
	p.ServeHTTP(rec, req)
	return rec
}

// The client must always see the legacy response, byte for byte, whatever the
// modern implementation does.
func TestClientAlwaysSeesLegacy(t *testing.T) {
	p := newTestProxy(t, jsonHandler(`{"v":"legacy"}`), jsonHandler(`{"v":"modern"}`), nil)
	defer p.Close()
	rec := get(p, "/x")
	if got := rec.Body.String(); got != `{"v":"legacy"}` {
		t.Fatalf("client saw %q", got)
	}
	if got := rec.Header().Get("Content-Type"); got != "application/json" {
		t.Errorf("headers were not passed through: %q", got)
	}
}

// The single most important refusal in the whole project.
func TestUnsafeMethodsAreNotMirroredByDefault(t *testing.T) {
	var writes atomic.Int64
	modern := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		writes.Add(1)
		w.Write([]byte(`{}`))
	})
	p := newTestProxy(t, jsonHandler(`{}`), modern, nil)

	for _, m := range []string{http.MethodPost, http.MethodPut, http.MethodPatch, http.MethodDelete} {
		req := httptest.NewRequest(m, "/orders", strings.NewReader(`{"amount":100}`))
		p.ServeHTTP(httptest.NewRecorder(), req)
	}
	p.Close()

	if got := writes.Load(); got != 0 {
		t.Fatalf("the modern implementation ran %d times on unsafe methods", got)
	}
	if got := p.Stats.SkippedUnsafe.Load(); got != 4 {
		t.Errorf("SkippedUnsafe = %d, want 4", got)
	}
	if got := p.Stats.Mirrored.Load(); got != 0 {
		t.Errorf("Mirrored = %d, want 0", got)
	}
}

// Opt-in per route, so the escape hatch exists but has to be taken deliberately.
func TestUnsafeMethodsCanBeOptedIn(t *testing.T) {
	var writes atomic.Int64
	modern := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		writes.Add(1)
		w.Write([]byte(`{}`))
	})
	p := newTestProxy(t, jsonHandler(`{}`), modern, func(c *Config) {
		c.MirrorUnsafe = func(r *http.Request) bool { return r.URL.Path == "/safe-to-replay" }
	})
	for _, path := range []string{"/safe-to-replay", "/orders"} {
		req := httptest.NewRequest(http.MethodPost, path, strings.NewReader(`{}`))
		p.ServeHTTP(httptest.NewRecorder(), req)
	}
	p.Close()

	if got := writes.Load(); got != 1 {
		t.Fatalf("modern ran %d times, want exactly the opted-in route", got)
	}
}

// A shadow proxy that buffers whatever it is handed is a memory-exhaustion bug
// with a nice name. The exclusion must also be counted, so "we compared all the
// traffic" stays a checkable claim.
func TestOversizedResponsesAreExcludedAndCounted(t *testing.T) {
	big := `{"blob":"` + strings.Repeat("x", 5000) + `"}`
	p := newTestProxy(t, jsonHandler(big), jsonHandler(big), func(c *Config) {
		c.MaxBodyBytes = 1024
	})
	rec := get(p, "/big")
	p.Close()

	if len(rec.Body.String()) != len(big) {
		t.Fatalf("the client got a truncated body: %d of %d bytes",
			len(rec.Body.String()), len(big))
	}
	if got := p.Stats.SkippedTooLarge.Load(); got != 1 {
		t.Errorf("SkippedTooLarge = %d, want 1", got)
	}
	if got := p.Stats.Compared.Load(); got != 0 {
		t.Errorf("Compared = %d, want 0", got)
	}
}

func TestDivergenceIsRecorded(t *testing.T) {
	var seen atomic.Int64
	var route atomic.Value
	tracker := confidence.NewTracker(confidence.DefaultPolicy())
	p := newTestProxy(t, jsonHandler(`{"v":1}`), jsonHandler(`{"v":2}`), func(c *Config) {
		c.Tracker = tracker
		c.OnDivergence = func(r string, diffs []jsondiff.Difference, l, m []byte) {
			seen.Add(1)
			route.Store(r)
		}
	})
	get(p, "/x")
	p.Close()

	if got := seen.Load(); got != 1 {
		t.Fatalf("OnDivergence fired %d times, want 1", got)
	}
	if got := route.Load(); got != "GET /x" {
		t.Errorf("route = %v, want GET /x", got)
	}
	if got := p.Stats.Diverged.Load(); got != 1 {
		t.Errorf("Diverged = %d, want 1", got)
	}
	if e := tracker.Endpoint("GET /x"); e.Total() != 1 || e.Matched() != 0 {
		t.Errorf("tracker recorded %d/%d, want 0/1", e.Matched(), e.Total())
	}
}

func TestMatchingResponsesAreNotDivergences(t *testing.T) {
	p := newTestProxy(t, jsonHandler(`{"v":1,"a":2}`), jsonHandler(`{"a":2,"v":1}`), nil)
	for i := 0; i < 5; i++ {
		get(p, "/x")
	}
	p.Close()
	if got := p.Stats.Diverged.Load(); got != 0 {
		t.Fatalf("key order was treated as a divergence %d times", got)
	}
	if got := p.Stats.Compared.Load(); got != 5 {
		t.Errorf("Compared = %d, want 5", got)
	}
}

// A different status code is a divergence even when the bodies agree.
func TestStatusCodeDivergence(t *testing.T) {
	modern := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusAccepted)
		w.Write([]byte(`{}`))
	})
	p := newTestProxy(t, jsonHandler(`{}`), modern, nil)
	get(p, "/x")
	p.Close()
	if got := p.Stats.Diverged.Load(); got != 1 {
		t.Fatalf("Diverged = %d, want 1", got)
	}
}

// A panic in the modern implementation is a finding, not an outage. The client
// has already been served by then and must not be affected.
func TestPanicInModernIsRecordedNotPropagated(t *testing.T) {
	modern := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		panic("modern exploded")
	})
	p := newTestProxy(t, jsonHandler(`{"v":1}`), modern, nil)
	rec := get(p, "/x")
	p.Close()

	if got := rec.Body.String(); got != `{"v":1}` {
		t.Fatalf("the client was affected by the panic: %q", got)
	}
	if got := p.Stats.ShadowErrors.Load(); got != 1 {
		t.Errorf("ShadowErrors = %d, want 1", got)
	}
	if got := p.Stats.Diverged.Load(); got != 1 {
		t.Errorf("a panic should count as a divergence, got %d", got)
	}
}

// Under load the shadow path sheds work rather than growing without bound, and
// says how much it shed.
func TestQueueFullDropsAreCounted(t *testing.T) {
	release := make(chan struct{})
	modern := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		<-release
		w.Write([]byte(`{}`))
	})
	p := newTestProxy(t, jsonHandler(`{}`), modern, func(c *Config) {
		c.Workers = 1
		c.QueueDepth = 2
	})
	for i := 0; i < 50; i++ {
		get(p, "/x")
	}
	dropped := p.Stats.DroppedQueue.Load()
	close(release)
	p.Close()

	if dropped == 0 {
		t.Fatal("nothing was dropped; the queue grew without bound")
	}
	if got := p.Stats.Requests.Load(); got != 50 {
		t.Errorf("Requests = %d, want 50", got)
	}
	if sum := p.Stats.Compared.Load() + dropped + p.Stats.ShadowErrors.Load(); sum > 50 {
		t.Errorf("accounting does not add up: compared+dropped+errors = %d of 50", sum)
	}
}

// Close must drain: a comparison that was accepted has to be finished, or the
// last few results before a shutdown vanish silently.
func TestCloseDrainsAcceptedWork(t *testing.T) {
	modern := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		time.Sleep(5 * time.Millisecond)
		w.Write([]byte(`{}`))
	})
	p := newTestProxy(t, jsonHandler(`{}`), modern, func(c *Config) {
		c.Workers = 2
		c.QueueDepth = 64
	})
	for i := 0; i < 20; i++ {
		get(p, "/x")
	}
	p.Close()
	if got := p.Stats.Compared.Load() + p.Stats.DroppedQueue.Load(); got != 20 {
		t.Fatalf("compared+dropped = %d, want 20; work was lost on shutdown", got)
	}
}

func TestCloseIsIdempotent(t *testing.T) {
	p := newTestProxy(t, jsonHandler(`{}`), jsonHandler(`{}`), nil)
	get(p, "/x")
	p.Close()
	p.Close()
}

// A slow modern implementation must not pin a shadow worker forever.
func TestShadowTimeout(t *testing.T) {
	modern := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		<-r.Context().Done()
	})
	p := newTestProxy(t, jsonHandler(`{}`), modern, func(c *Config) {
		c.ShadowTimeout = 20 * time.Millisecond
		c.Workers = 1
		c.QueueDepth = 4
	})
	done := make(chan struct{})
	go func() {
		get(p, "/x")
		p.Close()
		close(done)
	}()
	select {
	case <-done:
	case <-time.After(3 * time.Second):
		t.Fatal("Close blocked on a hung shadow request")
	}
}

// Routes are tracked separately, so one noisy endpoint cannot halt another.
func TestRoutesAreTrackedSeparately(t *testing.T) {
	legacy := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte(`{"v":1}`))
	})
	modern := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/bad" {
			w.Write([]byte(`{"v":2}`))
			return
		}
		w.Write([]byte(`{"v":1}`))
	})
	tracker := confidence.NewTracker(confidence.DefaultPolicy())
	p := newTestProxy(t, legacy, modern, func(c *Config) { c.Tracker = tracker })
	for i := 0; i < 10; i++ {
		get(p, "/good")
		get(p, "/bad")
	}
	p.Close()

	if e := tracker.Endpoint("GET /good"); e.Matched() != 10 {
		t.Errorf("/good matched %d of %d", e.Matched(), e.Total())
	}
	if e := tracker.Endpoint("GET /bad"); e.Matched() != 0 {
		t.Errorf("/bad matched %d of %d", e.Matched(), e.Total())
	}
}

// Once an endpoint reaches cutover the modern implementation serves the
// traffic, which is the only stage at which its response reaches a user.
func TestCutoverServesModern(t *testing.T) {
	tracker := confidence.NewTracker(confidence.DefaultPolicy())
	p := newTestProxy(t, jsonHandler(`{"v":"legacy"}`), jsonHandler(`{"v":"modern"}`),
		func(c *Config) { c.Tracker = tracker })
	defer p.Close()

	for i := 0; i < 3000; i++ {
		tracker.Record("GET /x", true, "")
	}
	if got := tracker.Endpoint("GET /x").Stage(); got != confidence.Cutover {
		t.Fatalf("setup: stage = %s, want cutover", got)
	}
	if got := get(p, "/x").Body.String(); got != `{"v":"modern"}` {
		t.Fatalf("at cutover the client should see modern, got %q", got)
	}
}

func TestStatsAccountForEveryRequest(t *testing.T) {
	p := newTestProxy(t, jsonHandler(`{}`), jsonHandler(`{}`), nil)
	for i := 0; i < 7; i++ {
		get(p, "/x")
	}
	for i := 0; i < 3; i++ {
		req := httptest.NewRequest(http.MethodPost, "/x", strings.NewReader("{}"))
		p.ServeHTTP(httptest.NewRecorder(), req)
	}
	p.Close()
	got := p.Stats.Requests.Load()
	accounted := p.Stats.Mirrored.Load() + p.Stats.SkippedUnsafe.Load() +
		p.Stats.SkippedTooLarge.Load()
	if got != 10 {
		t.Fatalf("Requests = %d, want 10", got)
	}
	if accounted != 10 {
		t.Fatalf("mirrored+skipped = %d, want 10; some requests are unaccounted for", accounted)
	}
}

func TestNonJSONResponsesAreStillCompared(t *testing.T) {
	p := newTestProxy(t, jsonHandler(`{"v":1}`),
		http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			w.Write([]byte(`<html>502</html>`))
		}), nil)
	get(p, "/x")
	p.Close()
	if got := p.Stats.Diverged.Load(); got != 1 {
		t.Fatalf("an HTML error page from the modern service should be a divergence, got %d", got)
	}
}

func mustJSON(t *testing.T, v any) string {
	t.Helper()
	b, err := json.Marshal(v)
	if err != nil {
		t.Fatal(err)
	}
	return string(b)
}

var _ = fmt.Sprintf
var _ = mustJSON
