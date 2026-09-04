// Package gateway ties the semantic cache and the single-flight group into the
// thing an application actually deploys, and — the point of the exercise —
// counts what each layer does to the answers.
//
// # Why the core is a discrete-event simulation
//
// Coalescing is a race by construction: whether request B joins request A's
// in-flight call depends on whether B arrives before A finishes. Measuring a
// coalescing policy with real goroutines and real sleeps produces numbers that
// change between runs, which makes every comparison in the report unfalsifiable.
//
// So the policy is evaluated on a virtual clock. Requests are processed in
// arrival order; a backend call started at t occupies [t, t+latency(q)); the
// in-flight set is retired against the current arrival time before each
// decision. The coalescing DECISIONS are therefore identical on every run, and
// so is every number in the report.
//
// That is not a substitute for the concurrent implementation, it is a
// complement to it. internal/flight is the real thing and is tested as the real
// thing (work-runs-once under a herd, prefix replay, leader abandonment). The
// gateway proves the policy is right; flight proves the mechanism is right.
// Conflating the two is how you end up unable to tell a policy regression from
// a scheduling artefact.
//
// # The metric this package exists to expose
//
// A semantic cache is usually judged by hit rate and false-hit rate, both
// measured at the cache. A gateway that also coalesces has a second path to a
// wrong answer: a request that MISSES the cache can still be attached to an
// in-flight call for a similar-but-different question, and be served that
// call's answer. That is a false hit in every sense that matters to the user,
// and it does not appear in any cache counter, because the cache was never
// asked. Stats separates Cache.FalseHits from CoalescedWrong precisely so the
// report can show the first looking healthy while the second is not.
package gateway

import (
	"hash/fnv"
	"sort"

	"semcache/internal/cache"
	"semcache/internal/corpus"
	"semcache/internal/embed"
)

// Mode selects the coalescing policy.
type Mode int

const (
	// CoalesceOff sends every miss to the backend.
	CoalesceOff Mode = iota
	// CoalesceExact merges requests whose normalised text is identical. This
	// is classic single-flight and cannot serve a wrong answer.
	CoalesceExact
	// CoalesceSemantic merges requests whose embeddings are close enough,
	// which is where the invisible errors come from.
	CoalesceSemantic
)

func (m Mode) String() string {
	switch m {
	case CoalesceOff:
		return "off"
	case CoalesceExact:
		return "exact"
	case CoalesceSemantic:
		return "semantic"
	}
	return "?"
}

// Config is the whole tunable surface of the gateway.
type Config struct {
	Cache cache.Config

	// Coalesce selects the in-flight merge policy.
	Coalesce Mode
	// CoalesceThreshold is the cosine floor for CoalesceSemantic. It is
	// separate from Cache.Threshold on purpose: a cache entry is reused
	// forever, an in-flight join is reused once, so an operator might
	// reasonably choose different risk for each. Whether that is WISE is one
	// of the things the report measures.
	CoalesceThreshold float64
	// CoalesceGuard applies the same rare-token guard to coalescing joins as
	// the cache applies to hits.
	CoalesceGuard bool

	// CacheCoalescedResults stores a coalesced follower's answer in the cache
	// under the FOLLOWER's text. This is what a naive implementation does —
	// it has an answer and a query, so it caches the pair — and it converts a
	// single transient mistake into a permanent one.
	CacheCoalescedResults bool

	// BackendLatencyUs bounds the simulated backend latency. A wider window
	// means longer in-flight intervals and therefore more coalescing.
	MinLatencyUs, MaxLatencyUs int64
}

// DefaultConfig is the configuration the report starts from.
func DefaultConfig() Config {
	return Config{
		Cache:             cache.Config{Threshold: 0.60, ScopeByTenant: true},
		Coalesce:          CoalesceExact,
		CoalesceThreshold: 0.60,
		MinLatencyUs:      20_000,
		MaxLatencyUs:      120_000,
	}
}

// Request is one arrival.
type Request struct {
	Text   string
	Tenant string
	// Intent is ground truth. It is used ONLY to score outcomes. Any read of
	// it on a decision path is a bug, and TestDecisionsIgnoreGroundTruth
	// scrambles this field across a whole workload to prove there isn't one.
	Intent   string
	ArriveUs int64
}

// Kind is how a request was served.
type Kind int

const (
	// KindBackend is a real backend call.
	KindBackend Kind = iota
	// KindExactHit is a cache hit on identical normalised text.
	KindExactHit
	// KindSemanticHit is a cache hit on similarity alone.
	KindSemanticHit
	// KindCoalesced is a request attached to an in-flight call.
	KindCoalesced
)

func (k Kind) String() string {
	switch k {
	case KindBackend:
		return "backend"
	case KindExactHit:
		return "exact-hit"
	case KindSemanticHit:
		return "semantic-hit"
	case KindCoalesced:
		return "coalesced"
	}
	return "?"
}

// Outcome is the full record of serving one request.
type Outcome struct {
	Kind Kind
	// ServedIntent is the intent whose answer the user actually received.
	ServedIntent string
	// TrueIntent is what they asked for.
	TrueIntent string
	// Correct is ServedIntent == TrueIntent.
	Correct bool
	// Similarity is the score that justified a hit or a join, if any.
	Similarity float64
	// GuardVetoed records that an above-threshold candidate was rejected by
	// the rare-token guard. These are the errors that did not happen.
	GuardVetoed bool
	// Confusable records that the served intent is in the same confusable
	// family as the true one — i.e. this was a hard case, not a random one.
	Confusable bool
}

// Stats is the counter block. Every request lands in exactly one of
// Backend/ExactHits/SemanticHits/Coalesced; the report asserts that.
type Stats struct {
	Requests       int
	Backend        int
	ExactHits      int
	SemanticHits   int
	Coalesced      int
	CacheFalse     int // wrong answers served BY THE CACHE
	CoalescedWrong int // wrong answers served BY COALESCING — invisible to the cache
	GuardVetoes    int
	Poisoned       int // cache entries written from a wrong coalesced answer
	Entries        int
	// ConfusableServed counts served-wrong cases where the served intent was
	// in the same confusable family. If most wrong answers are confusable,
	// the failure is systematic rather than accidental.
	ConfusableWrong int
}

// Hits is every answer that avoided a backend call.
func (s Stats) Hits() int { return s.ExactHits + s.SemanticHits + s.Coalesced }

// HitRate is the fraction of requests served without a backend call.
func (s Stats) HitRate() float64 {
	if s.Requests == 0 {
		return 0
	}
	return float64(s.Hits()) / float64(s.Requests)
}

// Wrong is every wrong answer from any path.
func (s Stats) Wrong() int { return s.CacheFalse + s.CoalescedWrong }

// Precision is the fraction of avoided backend calls that were correct. This,
// not hit rate, is the number an operator is actually risking.
func (s Stats) Precision() float64 {
	if s.Hits() == 0 {
		return 1
	}
	return 1 - float64(s.Wrong())/float64(s.Hits())
}

// CacheReportedPrecision is precision as the CACHE would report it: it can only
// see its own hits and its own mistakes. The gap between this and Precision is
// the blind spot.
func (s Stats) CacheReportedPrecision() float64 {
	ch := s.ExactHits + s.SemanticHits
	if ch == 0 {
		return 1
	}
	return 1 - float64(s.CacheFalse)/float64(ch)
}

// inflight is one simulated backend call occupying a virtual time interval.
type inflight struct {
	key    string
	vec    []float64
	text   string
	tenant string
	intent string // the LEADER's intent; followers inherit its answer
	endUs  int64
	rare   map[string]bool
}

// pendingPut is an answer that has been computed but has not yet reached the
// cache.
//
// This is the detail that makes or breaks the whole experiment. A cache is
// populated when the backend RETURNS, not when the request arrives. Filling it
// at arrival time - which is what the first version of this file did - means
// the second request of a burst finds the first one's answer already waiting,
// the cache hit rate jumps to 94%, and coalescing has nothing left to do,
// because the window it exists to cover has been defined out of existence.
// The bug was found by predicting a coalescing count and printing zero.
type pendingPut struct {
	entry cache.Entry
	atUs  int64
}

// Gateway is a configured cache + coalescer + backend.
type Gateway struct {
	cfg   Config
	model *embed.Model
	cache *cache.Cache

	live    []inflight
	pending []pendingPut
	st      Stats
}

// New builds a gateway over a fitted embedding model.
func New(cfg Config, model *embed.Model) *Gateway {
	return &Gateway{cfg: cfg, model: model, cache: cache.New(model, cfg.Cache)}
}

// Stats returns the counters.
func (g *Gateway) Stats() Stats {
	s := g.st
	s.Entries = g.cache.Len()
	return s
}

// Config returns the configuration, for reporting.
func (g *Gateway) Config() Config { return g.cfg }

// LatencyUs is the deterministic simulated backend latency for a query. It is
// a pure function of the text, so the in-flight intervals — and therefore every
// coalescing decision — are identical on every run.
func (g *Gateway) LatencyUs(text string) int64 {
	h := fnv.New64a()
	h.Write([]byte(text))
	span := g.cfg.MaxLatencyUs - g.cfg.MinLatencyUs
	if span <= 0 {
		return g.cfg.MinLatencyUs
	}
	return g.cfg.MinLatencyUs + int64(h.Sum64()%uint64(span))
}

// Run serves a workload in arrival order and returns one Outcome per request.
// The requests are sorted by arrival time; callers may pass them unsorted.
func (g *Gateway) Run(reqs []Request) []Outcome {
	sorted := make([]Request, len(reqs))
	copy(sorted, reqs)
	sort.SliceStable(sorted, func(i, j int) bool { return sorted[i].ArriveUs < sorted[j].ArriveUs })

	out := make([]Outcome, 0, len(sorted))
	for _, r := range sorted {
		out = append(out, g.serve(r))
	}
	// Drain the queue so Entries reports the steady-state cache, not whatever
	// happened to be in flight when the stream stopped.
	g.retire(int64(1) << 62)
	return out
}

func (g *Gateway) serve(r Request) Outcome {
	g.st.Requests++
	g.retire(r.ArriveUs)

	vec := g.model.Embed(r.Text)

	// 1. The cache. Nothing here may look at r.Intent.
	res := g.cache.Lookup(r.Text, r.Tenant)
	if res.GuardRejected {
		g.st.GuardVetoes++
	}
	if res.Hit {
		o := g.score(r, res.Entry.Intent, res.Similarity, res.GuardRejected)
		if res.Similarity >= 0.9999 && cache.ExactKey(res.Entry.Text) == cache.ExactKey(r.Text) {
			o.Kind = KindExactHit
			g.st.ExactHits++
		} else {
			o.Kind = KindSemanticHit
			g.st.SemanticHits++
		}
		if !o.Correct {
			g.st.CacheFalse++
		}
		return o
	}

	// 2. Coalescing. Also blind to r.Intent.
	if idx := g.joinable(r, vec); idx >= 0 {
		lead := g.live[idx]
		sim := embed.Cosine(vec, lead.vec)
		o := g.score(r, lead.intent, sim, false)
		o.Kind = KindCoalesced
		g.st.Coalesced++
		if !o.Correct {
			// The invisible error. The cache's own counters cannot see this,
			// because the cache was asked and correctly said "miss".
			g.st.CoalescedWrong++
		}
		if g.cfg.CacheCoalescedResults {
			g.enqueue(cache.Entry{Text: r.Text, Tenant: r.Tenant,
				Intent: lead.intent, Answer: corpus.Answer(lead.intent)}, lead.endUs)
			if !o.Correct {
				// A transient mistake is about to become a permanent entry.
				g.st.Poisoned++
			}
		}
		return o
	}

	// 3. The backend. The oracle: it always answers the question asked.
	g.st.Backend++
	endUs := r.ArriveUs + g.LatencyUs(r.Text)
	g.live = append(g.live, inflight{
		key:    cache.ExactKey(r.Text),
		vec:    vec,
		text:   r.Text,
		tenant: r.Tenant,
		intent: r.Intent,
		endUs:  endUs,
		rare:   g.rareSet(r.Text),
	})
	// The answer reaches the cache when the backend returns, not now.
	g.enqueue(cache.Entry{Text: r.Text, Tenant: r.Tenant,
		Intent: r.Intent, Answer: corpus.Answer(r.Intent)}, endUs)
	o := g.score(r, r.Intent, 1, false)
	o.Kind = KindBackend
	// A veto that sends a request to the backend is the ONLY veto that changed
	// an outcome, so it is the one that most needs to be visible per-outcome.
	// The flag used to be set only on the hit path, where res.GuardRejected is
	// almost never true, so Stats.GuardVetoes and the per-outcome flag
	// disagreed - Stats counted them, outcomes did not. A test comparing the
	// two found it.
	o.GuardVetoed = res.GuardRejected
	return o
}

func (g *Gateway) enqueue(e cache.Entry, atUs int64) {
	g.pending = append(g.pending, pendingPut{entry: e, atUs: atUs})
}

// score builds the Outcome. This is the ONLY place ground truth is read, and it
// runs after every decision has already been made.
func (g *Gateway) score(r Request, served string, sim float64, vetoed bool) Outcome {
	o := Outcome{
		ServedIntent: served,
		TrueIntent:   r.Intent,
		Correct:      served == r.Intent,
		Similarity:   sim,
		GuardVetoed:  vetoed,
	}
	if !o.Correct {
		o.Confusable = corpus.ConfusableIntents(served, r.Intent)
		if o.Confusable {
			g.st.ConfusableWrong++
		}
	}
	return o
}

// retire advances the virtual clock: it completes in-flight calls and applies
// the cache writes they produced. Order matters - a call that ends at exactly
// this instant has both stopped being joinable and started being cacheable.
func (g *Gateway) retire(nowUs int64) {
	keep := g.live[:0]
	for _, f := range g.live {
		if f.endUs > nowUs {
			keep = append(keep, f)
		}
	}
	g.live = keep

	pend := g.pending[:0]
	for _, p := range g.pending {
		if p.atUs <= nowUs {
			g.cache.Put(p.entry)
		} else {
			pend = append(pend, p)
		}
	}
	g.pending = pend
}

// joinable picks the in-flight call this request should attach to, or -1.
// Semantic mode picks the closest above threshold, which is the same rule the
// cache uses, for the same reason: a threshold plus an argmax is the only
// choice that does not depend on insertion order.
func (g *Gateway) joinable(r Request, vec []float64) int {
	switch g.cfg.Coalesce {
	case CoalesceOff:
		return -1
	case CoalesceExact:
		key := cache.ExactKey(r.Text)
		for i, f := range g.live {
			if f.key == key && (!g.cfg.Cache.ScopeByTenant || f.tenant == r.Tenant) {
				return i
			}
		}
		return -1
	}

	best, bestSim := -1, g.cfg.CoalesceThreshold
	for i, f := range g.live {
		if g.cfg.Cache.ScopeByTenant && f.tenant != r.Tenant {
			continue
		}
		sim := embed.Cosine(vec, f.vec)
		if sim >= bestSim {
			if g.cfg.CoalesceGuard && !g.rareCompatible(r.Text, f.rare) {
				g.st.GuardVetoes++
				continue
			}
			best, bestSim = i, sim
		}
	}
	return best
}

func (g *Gateway) rareSet(text string) map[string]bool {
	set := map[string]bool{}
	for _, t := range g.cache.DecisiveTokens(text) {
		set[t] = true
	}
	return set
}

// rareCompatible is the coalescing twin of cache.decisiveMatch.
func (g *Gateway) rareCompatible(text string, other map[string]bool) bool {
	mine := g.rareSet(text)
	if len(mine) == 0 && len(other) == 0 {
		return true
	}
	inter := 0
	for t := range mine {
		if other[t] {
			inter++
		}
	}
	union := len(mine) + len(other) - inter
	if union == 0 {
		return true
	}
	return float64(inter)/float64(union) >= g.cfg.Cache.GuardJaccard
}
