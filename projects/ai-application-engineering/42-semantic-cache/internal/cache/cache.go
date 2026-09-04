// Package cache is the semantic cache itself.
//
// The framing that drives every decision here: in an ordinary cache a wrong
// answer is impossible, so hit rate is the only metric. In a semantic cache a
// wrong answer is the *normal* consequence of a threshold set slightly too low,
// and it is not a performance regression - it is a correctness bug that returns
// someone else's answer to a paying customer, silently, with a 200 and a fast
// response time. Every metric in this package is therefore about precision
// first and hit rate second.
package cache

import (
	"sort"
	"strings"

	"semcache/internal/embed"
)

// Entry is a cached answer.
type Entry struct {
	Text   string
	Tenant string
	// Intent is ground truth. It is stored ONLY so the harness can score
	// whether a hit was correct. Lookup never reads it, and
	// TestLookupIgnoresIntent asserts that.
	Intent string
	Answer string
	Vec    embed.Vector
	Rare   []string
	// CostCents is what producing this answer cost, i.e. what a hit saves.
	CostCents float64
}

// Config is the cache's policy.
type Config struct {
	// ExactOnly disables similarity entirely: only byte-identical normalised
	// text hits. This is the baseline every semantic cache must beat, and it
	// is the only configuration that cannot serve a wrong answer.
	ExactOnly bool
	// Threshold is the minimum cosine similarity for a hit.
	Threshold float64
	// ScopeByTenant keys the cache per tenant. Off, a semantic cache is a
	// cross-tenant data leak with a latency benefit; see ADR 0004.
	ScopeByTenant bool
	// Guard enables the rare-token guard: a candidate is rejected unless its
	// decisive tokens match the query's. See ADR 0002.
	Guard bool
	// MinIDF is the cutoff above which a token counts as decisive. Section 4
	// of the report shows this is the WRONG knob on a small corpus, where the
	// IDF distribution is degenerate; GuardTopK is the one that works.
	MinIDF float64
	// GuardTopK, when > 0, defines the decisive set as the K highest-IDF
	// tokens of the query rather than every token above MinIDF. Bounding the
	// SIZE of the set is what makes the guard work when most of the
	// vocabulary is hapax.
	GuardTopK int
	// GuardJaccard is the minimum Jaccard overlap of decisive-token sets
	// required to accept a candidate. 1.0 demands an exact match.
	GuardJaccard float64
}

// Cache is a brute-force semantic cache.
//
// Brute force is deliberate. An approximate index (HNSW, IVF) is what
// production needs, and it introduces a *second* source of false hits and false
// misses on top of the threshold's. Measuring the threshold's contribution
// alone requires exact search; docs/known-limitations.md says what changes when
// you swap it out.
type Cache struct {
	model   *embed.Model
	cfg     Config
	entries []Entry
	// byTenant indexes into entries, so tenant scoping costs nothing.
	byTenant map[string][]int
	// byKey deduplicates on exact text within a scope. Without it a cache
	// "grows" one entry per request and its size stops meaning anything.
	byKey map[string]int
}

func New(model *embed.Model, cfg Config) *Cache {
	return &Cache{model: model, cfg: cfg,
		byTenant: map[string][]int{}, byKey: map[string]int{}}
}

// Config returns the cache's policy.
func (c *Cache) Config() Config { return c.cfg }

// Len is the number of cached entries.
func (c *Cache) Len() int { return len(c.entries) }

// Result describes what a lookup did.
type Result struct {
	Hit bool
	// Entry is the served entry when Hit.
	Entry Entry
	// Similarity is the best similarity seen, whether or not it cleared the
	// threshold. Reported on misses too, because "we missed at 0.87 with a 0.88
	// threshold" is the single most useful line in a cache log.
	Similarity float64
	// GuardRejected is true when a candidate cleared the similarity threshold
	// and the decisive-token guard vetoed it. This is the counter that says
	// whether the guard is earning its keep or just adding latency.
	GuardRejected bool
	// RejectedEntry is that vetoed candidate, for the report.
	RejectedEntry Entry
}

// Lookup finds the best cached answer for a query.
func (c *Cache) Lookup(text, tenant string) Result {
	if c.cfg.ExactOnly {
		if i, ok := c.byKey[c.scopeKey(tenant)+"\x00"+ExactKey(text)]; ok {
			return Result{Hit: true, Entry: c.entries[i], Similarity: 1}
		}
		return Result{}
	}

	vec := c.model.Embed(text)
	rare := c.decisiveTokens(text)

	candidates := c.candidateIdx(tenant)
	best, bestSim := -1, -1.0
	guardBlocked, guardSim := -1, -1.0

	for _, i := range candidates {
		sim := embed.Cosine(vec, c.entries[i].Vec)
		if sim < c.cfg.Threshold {
			if sim > bestSim && best < 0 {
				bestSim = sim
			}
			continue
		}
		if c.cfg.Guard && !decisiveMatch(rare, c.entries[i].Rare, c.cfg.GuardJaccard) {
			if sim > guardSim {
				guardBlocked, guardSim = i, sim
			}
			continue
		}
		if sim > bestSim {
			best, bestSim = i, sim
		}
	}

	if best >= 0 {
		return Result{Hit: true, Entry: c.entries[best], Similarity: bestSim}
	}
	if guardBlocked >= 0 {
		return Result{Similarity: guardSim, GuardRejected: true,
			RejectedEntry: c.entries[guardBlocked]}
	}
	return Result{Similarity: bestSim}
}

// DecisiveTokens exposes the guard's view of a query, so the gateway's
// coalescing guard uses exactly the same definition as the cache's. Two
// implementations of "decisive" that drift apart would make the report's
// comparison between them meaningless.
func (c *Cache) DecisiveTokens(text string) []string { return c.decisiveTokens(text) }

func (c *Cache) decisiveTokens(text string) []string {
	if !c.cfg.Guard {
		return nil
	}
	if c.cfg.GuardTopK > 0 {
		return c.model.TopIDFTokens(text, c.cfg.GuardTopK)
	}
	return c.model.RareTokens(text, c.cfg.MinIDF)
}

// Put stores an answer, replacing any entry for the same normalised text in
// the same scope. A cache that appends a fresh entry per request is not a
// cache; its size is a request counter and its lookups get slower forever.
func (c *Cache) Put(e Entry) {
	e.Vec = c.model.Embed(e.Text)
	e.Rare = c.decisiveTokens(e.Text)

	scope := c.scopeKey(e.Tenant)
	key := scope + "\x00" + ExactKey(e.Text)
	if i, ok := c.byKey[key]; ok {
		c.entries[i] = e
		return
	}
	c.entries = append(c.entries, e)
	c.byKey[key] = len(c.entries) - 1
	c.byTenant[scope] = append(c.byTenant[scope], len(c.entries)-1)
}

func (c *Cache) candidateIdx(tenant string) []int {
	return c.byTenant[c.scopeKey(tenant)]
}

func (c *Cache) scopeKey(tenant string) string {
	if c.cfg.ScopeByTenant {
		return tenant
	}
	return ""
}

// decisiveMatch is the guard.
//
// The argument for it is that cosine similarity is a weighted MEAN over token
// contributions, and a mean cannot express "this one token decides the answer".
// Changing "germany" to "france" in a nine-token query moves the mean by that
// token's share of the vector mass and no more - a bound this project computes
// explicitly in section 2. So the fix is not a better threshold on the mean. It
// is to stop taking a mean over the tokens that matter, and compare them
// directly.
//
// Rare tokens are the right set to compare because IDF is exactly a measure of
// how much a token narrows the space of possible answers.
func decisiveMatch(a, b []string, minJaccard float64) bool {
	if len(a) == 0 && len(b) == 0 {
		return true
	}
	set := map[string]bool{}
	for _, x := range a {
		set[x] = true
	}
	inter := 0
	for _, y := range b {
		if set[y] {
			inter++
		}
	}
	union := len(a) + len(b) - inter
	if union == 0 {
		return true
	}
	return float64(inter)/float64(union) >= minJaccard
}

// ExactKey normalises a query for exact-match caching, which is the baseline
// every semantic cache has to beat. Lowercase, collapse whitespace, drop
// trailing punctuation. Nothing clever, because nothing clever is the point.
func ExactKey(text string) string {
	f := strings.Fields(strings.ToLower(strings.TrimSpace(text)))
	for i, w := range f {
		f[i] = strings.Trim(w, ".,!?;:")
	}
	return strings.Join(f, " ")
}

// Jaccard is exported for the report's diagnostics.
func Jaccard(a, b []string) float64 {
	set := map[string]bool{}
	for _, x := range a {
		set[x] = true
	}
	inter := 0
	seen := map[string]bool{}
	for _, y := range b {
		if set[y] && !seen[y] {
			inter++
			seen[y] = true
		}
	}
	union := len(dedup(a)) + len(dedup(b)) - inter
	if union == 0 {
		return 1
	}
	return float64(inter) / float64(union)
}

func dedup(in []string) []string {
	seen := map[string]bool{}
	var out []string
	for _, x := range in {
		if !seen[x] {
			seen[x] = true
			out = append(out, x)
		}
	}
	sort.Strings(out)
	return out
}
