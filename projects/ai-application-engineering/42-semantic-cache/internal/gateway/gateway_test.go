package gateway

import (
	"math/rand"
	"testing"

	"semcache/internal/cache"
	"semcache/internal/corpus"
	"semcache/internal/embed"
)

func model() *embed.Model {
	var texts []string
	for _, q := range corpus.All() {
		texts = append(texts, q.Text)
	}
	return embed.Fit(texts, embed.Lexical, 4096, 96, 11)
}

// stream builds a deterministic request stream directly from the corpus so the
// gateway tests do not depend on the workload generator.
func stream(n int, seed int64) []Request {
	all := corpus.All()
	rng := rand.New(rand.NewSource(seed))
	reqs := make([]Request, 0, n)
	var t int64
	for i := 0; i < n; i++ {
		q := all[rng.Intn(len(all))]
		t += int64(rng.Intn(500))
		reqs = append(reqs, Request{
			Text: q.Text, Tenant: q.Tenant, Intent: q.Intent, ArriveUs: t,
		})
	}
	return reqs
}

// heldOutStream warms the cache with the FIRST phrasing of each intent and then
// asks only the OTHER phrasings. A stream sampled uniformly from the corpus is
// useless for testing the guard: after a few hundred requests every text is
// cached verbatim, every lookup is an exact hit, and the guard never has to
// adjudicate anything. That is the same "82% verbatim repeats" effect the
// report's section 4 is about, and it silently turns tests into no-ops.
func heldOutStream(reps int) []Request {
	byIntent := map[string][]corpus.Query{}
	for _, q := range corpus.All() {
		byIntent[q.Intent] = append(byIntent[q.Intent], q)
	}
	var warm, rest []corpus.Query
	for _, intent := range corpus.Intents() {
		qs := byIntent[intent]
		warm = append(warm, qs[0])
		rest = append(rest, qs[1:]...)
	}
	var reqs []Request
	var t int64
	push := func(q corpus.Query) {
		t += 1_000_000 // far enough apart that nothing coalesces
		reqs = append(reqs, Request{Text: q.Text, Tenant: q.Tenant, Intent: q.Intent, ArriveUs: t})
	}
	for _, q := range warm {
		push(q)
	}
	for i := 0; i < reps; i++ {
		for _, q := range rest {
			push(q)
		}
	}
	return reqs
}

func cfg() Config {
	c := DefaultConfig()
	c.Cache.Threshold = 0.6
	return c
}

// THE integrity test for the whole experiment.
//
// Request.Intent is ground truth. It exists so outcomes can be SCORED. If it
// ever influenced a decision, every hit rate, false-hit rate and precision
// number in the report would be manufactured. Scrambling every label must
// leave the decision sequence byte-identical.
func TestDecisionsIgnoreGroundTruth(t *testing.T) {
	m := model()
	reqs := stream(1500, 7)

	kinds := func(rs []Request) []Kind {
		g := New(cfg(), m)
		outs := g.Run(rs)
		out := make([]Kind, len(outs))
		for i, o := range outs {
			out[i] = o.Kind
		}
		return out
	}

	scrambled := make([]Request, len(reqs))
	copy(scrambled, reqs)
	rng := rand.New(rand.NewSource(99))
	intents := corpus.Intents()
	for i := range scrambled {
		scrambled[i].Intent = intents[rng.Intn(len(intents))]
	}

	a, b := kinds(reqs), kinds(scrambled)
	for i := range a {
		if a[i] != b[i] {
			t.Fatalf("decision %d changed from %v to %v when the ground-truth labels "+
				"were scrambled. The gateway is reading the answer key; every number "+
				"in the report is manufactured.", i, a[i], b[i])
		}
	}
}

func TestRunIsDeterministic(t *testing.T) {
	m := model()
	reqs := stream(800, 3)
	a := New(cfg(), m).Run(reqs)
	b := New(cfg(), m).Run(reqs)
	if len(a) != len(b) {
		t.Fatalf("different lengths: %d vs %d", len(a), len(b))
	}
	for i := range a {
		if a[i] != b[i] {
			t.Fatalf("outcome %d differs between identical runs: %+v vs %+v", i, a[i], b[i])
		}
	}
}

// Every request must be accounted for exactly once. Without this, a change that
// silently drops requests would look like an improvement in every rate.
func TestEveryRequestIsAccountedForExactlyOnce(t *testing.T) {
	m := model()
	reqs := stream(1200, 5)
	for _, mode := range []Mode{CoalesceOff, CoalesceExact, CoalesceSemantic} {
		c := cfg()
		c.Coalesce = mode
		c.CoalesceThreshold = 0.7
		g := New(c, m)
		outs := g.Run(reqs)
		st := g.Stats()
		if len(outs) != len(reqs) {
			t.Errorf("%v: %d outcomes for %d requests", mode, len(outs), len(reqs))
		}
		if st.Requests != len(reqs) {
			t.Errorf("%v: Stats.Requests = %d, want %d", mode, st.Requests, len(reqs))
		}
		sum := st.Backend + st.ExactHits + st.SemanticHits + st.Coalesced
		if sum != st.Requests {
			t.Errorf("%v: backend %d + exact %d + semantic %d + coalesced %d = %d, "+
				"but there were %d requests", mode, st.Backend, st.ExactHits,
				st.SemanticHits, st.Coalesced, sum, st.Requests)
		}
	}
}

func TestWrongAnswersSplitIntoCacheAndCoalescing(t *testing.T) {
	m := model()
	c := cfg()
	c.Cache.Threshold = 0.45
	c.Coalesce = CoalesceSemantic
	c.CoalesceThreshold = 0.45
	g := New(c, m)
	outs := g.Run(stream(1500, 11))
	st := g.Stats()

	wrong := 0
	for _, o := range outs {
		if !o.Correct {
			wrong++
		}
	}
	if got := st.Wrong(); got != wrong {
		t.Errorf("Stats.Wrong() = %d but %d outcomes were incorrect", got, wrong)
	}
	if st.CacheFalse+st.CoalescedWrong != wrong {
		t.Errorf("CacheFalse %d + CoalescedWrong %d != %d wrong outcomes",
			st.CacheFalse, st.CoalescedWrong, wrong)
	}
}

// Section 6's whole argument. A coalesced wrong answer is invisible to the
// cache's own precision metric because the request was a cache MISS.
func TestCacheReportedPrecisionExcludesCoalescingErrors(t *testing.T) {
	m := model()
	c := cfg()
	c.Cache.Threshold = 0.6
	c.Coalesce = CoalesceSemantic
	c.CoalesceThreshold = 0.30 // deliberately reckless
	g := New(c, m)
	g.Run(stream(2000, 13))
	st := g.Stats()

	if st.CoalescedWrong == 0 {
		t.Fatal("a coalescing threshold of 0.30 produced no wrong answers; the test " +
			"cannot demonstrate the blind spot")
	}
	if st.CacheReportedPrecision() <= st.Precision() {
		t.Errorf("cache-reported precision %.4f should exceed true precision %.4f when "+
			"coalescing is serving wrong answers", st.CacheReportedPrecision(), st.Precision())
	}
}

// The bug this test exists to prevent was the most important one in the
// project: the gateway used to fill the cache when a request ARRIVED rather
// than when its backend call COMPLETED, which defines away the exact window
// coalescing exists to cover.
func TestCacheIsFilledOnCompletionNotOnArrival(t *testing.T) {
	m := model()
	q := corpus.All()[0]
	c := cfg()
	c.Cache.ExactOnly = true
	c.Coalesce = CoalesceOff // otherwise the second request joins the flight
	g := New(c, m)

	lat := g.LatencyUs(q.Text)
	// Two identical requests, the second arriving strictly before the first
	// could possibly have finished.
	outs := g.Run([]Request{
		{Text: q.Text, Tenant: q.Tenant, Intent: q.Intent, ArriveUs: 0},
		{Text: q.Text, Tenant: q.Tenant, Intent: q.Intent, ArriveUs: lat / 2},
	})
	if outs[1].Kind != KindBackend {
		t.Errorf("second request at t=%d was served as %v, but the first request's "+
			"answer does not exist until t=%d. The cache is being filled on arrival.",
			lat/2, outs[1].Kind, lat)
	}

	g2 := New(c, m)
	outs2 := g2.Run([]Request{
		{Text: q.Text, Tenant: q.Tenant, Intent: q.Intent, ArriveUs: 0},
		{Text: q.Text, Tenant: q.Tenant, Intent: q.Intent, ArriveUs: lat + 1},
	})
	if outs2[1].Kind == KindBackend {
		t.Error("second request after the first completed should have hit the cache")
	}
}

func TestCoalescingRequiresOverlappingRequests(t *testing.T) {
	m := model()
	q := corpus.All()[0]
	c := cfg()
	c.Coalesce = CoalesceExact
	g := New(c, m)
	lat := g.LatencyUs(q.Text)

	// Far apart: nothing to join.
	g.Run([]Request{
		{Text: q.Text, Tenant: q.Tenant, Intent: q.Intent, ArriveUs: 0},
		{Text: q.Text, Tenant: q.Tenant, Intent: q.Intent, ArriveUs: 10 * lat},
	})
	if g.Stats().Coalesced != 0 {
		t.Error("requests separated by 10x the backend latency were coalesced")
	}

	g2 := New(c, m)
	g2.Run([]Request{
		{Text: q.Text, Tenant: q.Tenant, Intent: q.Intent, ArriveUs: 0},
		{Text: q.Text, Tenant: q.Tenant, Intent: q.Intent, ArriveUs: lat / 2},
	})
	if g2.Stats().Coalesced != 1 {
		t.Errorf("an overlapping duplicate was not coalesced (Coalesced=%d)",
			g2.Stats().Coalesced)
	}
}

func TestCoalescingNeverIncreasesBackendCalls(t *testing.T) {
	m := model()
	reqs := stream(1500, 17)
	base := New(cfg(), m)
	base.Run(reqs)
	off := base.Stats().Backend

	for _, mode := range []Mode{CoalesceExact, CoalesceSemantic} {
		c := cfg()
		c.Coalesce = mode
		c.CoalesceThreshold = 0.75
		g := New(c, m)
		g.Run(reqs)
		if got := g.Stats().Backend; got > off {
			t.Errorf("%v made %d backend calls, more than %d with coalescing off",
				mode, got, off)
		}
	}
}

func TestCachingCoalescedResultsCreatesPoisonedEntries(t *testing.T) {
	m := model()
	reqs := stream(2000, 19)
	c := cfg()
	c.Coalesce = CoalesceSemantic
	c.CoalesceThreshold = 0.30
	c.CacheCoalescedResults = true
	g := New(c, m)
	g.Run(reqs)
	if g.Stats().Poisoned == 0 {
		t.Error("CacheCoalescedResults with a reckless threshold produced no poisoned " +
			"entries; section 6's claim that a transient mistake becomes a permanent " +
			"one is untested")
	}

	c.CacheCoalescedResults = false
	g2 := New(c, m)
	g2.Run(reqs)
	if g2.Stats().Poisoned != 0 {
		t.Errorf("Poisoned = %d with CacheCoalescedResults off", g2.Stats().Poisoned)
	}
}

func TestExactOnlyNeverServesADifferentIntent(t *testing.T) {
	m := model()
	c := cfg()
	c.Cache.ExactOnly = true
	c.Cache.ScopeByTenant = true
	g := New(c, m)
	outs := g.Run(stream(2000, 23))
	for i, o := range outs {
		if o.Kind != KindBackend && !o.Correct {
			t.Fatalf("outcome %d: an exact-key cache scoped by tenant served a wrong "+
				"answer (%s for %s). That is only possible if two different intents "+
				"share a normalised key within one tenant.",
				i, o.ServedIntent, o.TrueIntent)
		}
	}
}

// Tenant scoping's guarantee is structural, not statistical: a scoped lookup
// can only ever consider entries belonging to that tenant, so the answer served
// must be an intent that tenant actually has.
func TestScopedAnswersAlwaysBelongToTheAskingTenant(t *testing.T) {
	m := model()
	has := map[string]map[string]bool{}
	for _, q := range corpus.All() {
		if has[q.Tenant] == nil {
			has[q.Tenant] = map[string]bool{}
		}
		has[q.Tenant][q.Intent] = true
	}

	c := cfg()
	c.Cache.ScopeByTenant = true
	c.Cache.Threshold = 0.5
	c.Coalesce = CoalesceOff // coalescing is a separate path with its own scope rules
	g := New(c, m)
	for i, o := range g.Run(heldOutStream(2)) {
		if o.Kind == KindBackend {
			continue
		}
		if !has[reqTenant(i)][o.ServedIntent] {
			t.Fatalf("outcome %d served intent %q to tenant %q, which does not have it",
				i, o.ServedIntent, reqTenant(i))
		}
	}
}

// reqTenant recovers the asking tenant for outcome i of heldOutStream(2).
func reqTenant(i int) string { return heldOutStreamCache[i].Tenant }

var heldOutStreamCache = heldOutStream(2)

// Without scoping, the corpus's identical-wording trap leaks: the same bytes
// mean different things to different tenants, and no encoder can tell.
func TestUnscopedCacheLeaksAcrossTenants(t *testing.T) {
	m := model()
	c := cfg()
	c.Cache.ScopeByTenant = false
	c.Cache.Threshold = 0.6
	c.Coalesce = CoalesceOff
	g := New(c, m)

	has := map[string]map[string]bool{}
	for _, q := range corpus.All() {
		if has[q.Tenant] == nil {
			has[q.Tenant] = map[string]bool{}
		}
		has[q.Tenant][q.Intent] = true
	}
	leaks := 0
	for i, o := range g.Run(heldOutStreamCache) {
		if o.Kind == KindBackend {
			continue
		}
		if !has[heldOutStreamCache[i].Tenant][o.ServedIntent] {
			leaks++
		}
	}
	if leaks == 0 {
		t.Error("an unscoped cache produced no cross-tenant answers; the corpus's " +
			"identical-wording trap is not doing its job and section 5 proves nothing")
	}
}

func TestHigherThresholdNeverRaisesTheHitRate(t *testing.T) {
	m := model()
	reqs := stream(1500, 31)
	prev := 2.0
	for _, thr := range []float64{0.2, 0.4, 0.6, 0.8, 0.95} {
		c := cfg()
		c.Cache.Threshold = thr
		g := New(c, m)
		g.Run(reqs)
		hr := g.Stats().HitRate()
		if hr > prev+1e-12 {
			t.Errorf("threshold %v gave hit rate %.4f, above the looser cut's %.4f",
				thr, hr, prev)
		}
		prev = hr
	}
}

func TestEntriesNeverExceedDistinctTexts(t *testing.T) {
	m := model()
	reqs := stream(4000, 37)
	distinct := map[string]bool{}
	for _, r := range reqs {
		distinct[cache.ExactKey(r.Text)+"|"+r.Tenant] = true
	}
	c := cfg()
	c.Cache.ScopeByTenant = true
	g := New(c, m)
	g.Run(reqs)
	if e := g.Stats().Entries; e > len(distinct) {
		t.Errorf("%d entries for %d distinct (text,tenant) pairs across %d requests; "+
			"the cache is accumulating duplicates", e, len(distinct), len(reqs))
	}
}

// GuardVetoes counts vetoes that CHANGED AN OUTCOME - the guard blocked the
// last remaining candidate and the request went to the backend. If a correct
// entry is also above threshold it simply wins, and no veto is recorded. So
// firing the guard requires a stream where the only available answer is the
// wrong one: warm one member of each confusable family, then ask its sibling.
// This is the same shape as the report's trap probe.
func TestGuardVetoesAreCountedWhenTheyChangeAnOutcome(t *testing.T) {
	m := model()
	byIntent := map[string][]corpus.Query{}
	for _, q := range corpus.All() {
		byIntent[q.Intent] = append(byIntent[q.Intent], q)
	}
	var reqs []Request
	var t0 int64
	seen := map[string]bool{}
	for _, a := range corpus.Intents() {
		for _, b := range corpus.Intents() {
			if a >= b || !corpus.ConfusableIntents(a, b) || seen[a] {
				continue
			}
			qa, qb := byIntent[a][0], byIntent[b][0]
			if qa.Tenant != qb.Tenant {
				continue
			}
			seen[a] = true
			t0 += 1_000_000
			reqs = append(reqs, Request{Text: qa.Text, Tenant: qa.Tenant, Intent: qa.Intent, ArriveUs: t0})
			t0 += 1_000_000
			reqs = append(reqs, Request{Text: qb.Text, Tenant: qb.Tenant, Intent: qb.Intent, ArriveUs: t0})
		}
	}
	if len(reqs) < 10 {
		t.Fatalf("only %d trap requests could be built", len(reqs))
	}

	base := cfg()
	base.Cache.Threshold = 0.45
	base.Coalesce = CoalesceOff
	unguarded := New(base, m)
	unguarded.Run(reqs)
	if unguarded.Stats().CacheFalse == 0 {
		t.Fatal("the unguarded cache made no mistakes on the trap stream; there is " +
			"nothing for the guard to prevent")
	}

	c := base
	c.Cache.Guard = true
	c.Cache.GuardTopK = 3
	c.Cache.GuardJaccard = 0.67
	g := New(c, m)
	outs := g.Run(reqs)

	seenVeto := 0
	for _, o := range outs {
		if o.GuardVetoed {
			seenVeto++
		}
	}
	if seenVeto == 0 {
		t.Fatal("no vetoes fired on a stream built entirely from confusable siblings")
	}
	if g.Stats().GuardVetoes != seenVeto {
		t.Errorf("Stats.GuardVetoes = %d but %d outcomes were flagged",
			g.Stats().GuardVetoes, seenVeto)
	}
	if g.Stats().CacheFalse >= unguarded.Stats().CacheFalse {
		t.Errorf("the guard did not reduce wrong answers: %d with, %d without",
			g.Stats().CacheFalse, unguarded.Stats().CacheFalse)
	}
}

func TestEmptyWorkloadIsHandled(t *testing.T) {
	g := New(cfg(), model())
	if outs := g.Run(nil); len(outs) != 0 {
		t.Errorf("Run(nil) returned %d outcomes", len(outs))
	}
	st := g.Stats()
	if st.HitRate() != 0 {
		t.Errorf("empty run should report a zero hit rate, got %v", st.HitRate())
	}
	// Precision over zero hits is vacuously 1. This is mathematically right and
	// operationally a trap - a configuration that never hits displays as
	// flawless - so it is pinned here and called out in known-limitations
	// rather than quietly changed.
	if st.Precision() != 1 {
		t.Errorf("Precision() over zero hits = %v, want the documented vacuous 1",
			st.Precision())
	}
}

func TestOutOfOrderArrivalsAreProcessedInTimeOrder(t *testing.T) {
	m := model()
	reqs := stream(600, 43)
	shuffled := make([]Request, len(reqs))
	copy(shuffled, reqs)
	rand.New(rand.NewSource(2)).Shuffle(len(shuffled), func(i, j int) {
		shuffled[i], shuffled[j] = shuffled[j], shuffled[i]
	})
	a := New(cfg(), m)
	a.Run(reqs)
	b := New(cfg(), m)
	b.Run(shuffled)
	if a.Stats() != b.Stats() {
		t.Error("shuffling the input changed the results; Run does not sort by arrival " +
			"time, so the simulation depends on slice order rather than the clock")
	}
}

func TestModeAndKindStringsAreDistinct(t *testing.T) {
	seen := map[string]bool{}
	for _, m := range []Mode{CoalesceOff, CoalesceExact, CoalesceSemantic} {
		if seen[m.String()] {
			t.Errorf("duplicate Mode string %q", m.String())
		}
		seen[m.String()] = true
	}
	seen = map[string]bool{}
	for _, k := range []Kind{KindBackend, KindExactHit, KindSemanticHit, KindCoalesced} {
		if seen[k.String()] {
			t.Errorf("duplicate Kind string %q", k.String())
		}
		seen[k.String()] = true
	}
}

func TestLatencyIsPositiveAndDeterministic(t *testing.T) {
	g := New(cfg(), model())
	for _, q := range corpus.All()[:30] {
		a := g.LatencyUs(q.Text)
		if a <= 0 {
			t.Fatalf("latency for %q is %d", q.Text, a)
		}
		if b := g.LatencyUs(q.Text); b != a {
			t.Fatalf("latency for %q is not deterministic: %d then %d", q.Text, a, b)
		}
	}
}
