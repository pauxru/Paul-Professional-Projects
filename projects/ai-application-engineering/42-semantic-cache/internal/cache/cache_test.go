package cache

import (
	"math/rand"
	"testing"

	"semcache/internal/corpus"
	"semcache/internal/embed"
)

func texts() []string {
	var out []string
	for _, q := range corpus.All() {
		out = append(out, q.Text)
	}
	return out
}

func model() *embed.Model {
	return embed.Fit(texts(), embed.Lexical, 4096, 96, 11)
}

// find returns the first corpus query with this text. Where a text appears
// under several tenants the caller must use findFor instead.
func find(text string) corpus.Query {
	for _, q := range corpus.All() {
		if q.Text == text {
			return q
		}
	}
	return corpus.Query{Text: text, Intent: "unknown", Tenant: "acme"}
}

func findFor(text, tenant string) corpus.Query {
	for _, q := range corpus.All() {
		if q.Text == text && q.Tenant == tenant {
			return q
		}
	}
	return corpus.Query{Text: text, Intent: "unknown", Tenant: tenant}
}

// identicalWordingTrap finds a text that appears verbatim under two or more
// tenants with different intents. This is the one confusion no encoder can
// resolve, and the reason scoping is a correctness feature.
func identicalWordingTrap() (string, []string) {
	byText := map[string][]corpus.Query{}
	for _, q := range corpus.All() {
		byText[q.Text] = append(byText[q.Text], q)
	}
	for _, q := range corpus.All() {
		qs := byText[q.Text]
		if len(qs) < 2 {
			continue
		}
		var tenants []string
		seenIntent := map[string]bool{}
		for _, x := range qs {
			tenants = append(tenants, x.Tenant)
			seenIntent[x.Intent] = true
		}
		if len(seenIntent) >= 2 {
			return q.Text, tenants
		}
	}
	return "", nil
}

// confusablePairs returns text pairs from different intents in the same
// confusable family - the population the cache must never conflate.
func confusablePairs() [][2]string {
	all := corpus.All()
	var out [][2]string
	for i, a := range all {
		for _, b := range all[i+1:] {
			if a.Tenant == b.Tenant && a.Intent != b.Intent &&
				corpus.ConfusableIntents(a.Intent, b.Intent) {
				out = append(out, [2]string{a.Text, b.Text})
			}
		}
	}
	return out
}

// confusablePair returns two texts from different intents in the same
// confusable family: the shipping-to-Germany / shipping-to-France shape.
func confusablePair() (string, string) {
	if p := confusablePairs(); len(p) > 0 {
		return p[0][0], p[0][1]
	}
	return "", ""
}

// paraphrasePairs returns distinct texts that share an intent - the traffic the
// cache exists to serve, and the population a guard must not destroy.
func paraphrasePairs() [][2]string {
	byIntent := map[string][]corpus.Query{}
	for _, q := range corpus.All() {
		byIntent[q.Intent] = append(byIntent[q.Intent], q)
	}
	var out [][2]string
	for _, intent := range corpus.Intents() {
		qs := byIntent[intent]
		for i := 0; i+1 < len(qs); i++ {
			if qs[i].Tenant == qs[i+1].Tenant && qs[i].Text != qs[i+1].Text {
				out = append(out, [2]string{qs[i].Text, qs[i+1].Text})
			}
		}
	}
	return out
}

func entry(text, tenant, intent string) Entry {
	return Entry{Text: text, Tenant: tenant, Intent: intent, Answer: corpus.Answer(intent)}
}

func build(cfg Config, ts ...string) *Cache {
	m := model()
	c := New(m, cfg)
	for _, t := range ts {
		q := find(t)
		e := entry(t, q.Tenant, q.Intent)
		e.Vec = m.Embed(t)
		e.Rare = c.decisiveTokens(t)
		c.Put(e)
	}
	return c
}

func cfg() Config { return Config{Threshold: 0.6} }

// THE integrity test for the cache. A cache cannot see the label - it only has
// the text. If scrambling every stored Intent changed a single hit/miss
// decision, the lookup path would be reading ground truth and every measured
// hit rate in this project would be fiction.
func TestLookupDecisionsIgnoreStoredIntents(t *testing.T) {
	all := texts()
	m := model()

	decisions := func(scramble bool) []bool {
		c := New(m, cfg())
		rng := rand.New(rand.NewSource(4))
		for _, txt := range all[:80] {
			q := find(txt)
			intent := q.Intent
			if scramble {
				intent = "scrambled-" + corpus.Intents()[rng.Intn(len(corpus.Intents()))]
			}
			e := entry(txt, q.Tenant, intent)
			e.Vec = m.Embed(txt)
			e.Rare = c.decisiveTokens(txt)
			c.Put(e)
		}
		var out []bool
		for _, txt := range all {
			out = append(out, c.Lookup(txt, find(txt).Tenant).Hit)
		}
		return out
	}

	honest, scrambled := decisions(false), decisions(true)
	for i := range honest {
		if honest[i] != scrambled[i] {
			t.Fatalf("decision %d changed when the stored labels were scrambled: the "+
				"cache is reading ground truth, and every hit rate here is fiction", i)
		}
	}
}

func TestPutReplacesRatherThanAppends(t *testing.T) {
	m := model()
	c := New(m, cfg())
	for i := 0; i < 50; i++ {
		e := entry("how long does shipping take to germany", "acme", "ship-de")
		e.Vec = m.Embed(e.Text)
		c.Put(e)
	}
	if c.Len() != 1 {
		t.Errorf("50 puts of one text produced %d entries; a cache whose size tracks "+
			"its request count is a log, not a cache", c.Len())
	}
}

func TestPutKeepsDistinctTextsSeparate(t *testing.T) {
	ts := texts()[:12]
	c := build(cfg(), ts...)
	if c.Len() != 12 {
		t.Errorf("12 distinct texts produced %d entries", c.Len())
	}
}

func TestExactKeyIgnoresCaseSpacingAndPunctuation(t *testing.T) {
	same := [][2]string{
		{"How long does shipping take?", "how long does shipping take"},
		{"  refund   my order  ", "refund my order"},
		{"CANCEL my PLAN!", "cancel my plan"},
	}
	for _, p := range same {
		if ExactKey(p[0]) != ExactKey(p[1]) {
			t.Errorf("ExactKey(%q) != ExactKey(%q)", p[0], p[1])
		}
	}
	if ExactKey("shipping to germany") == ExactKey("shipping to france") {
		t.Error("ExactKey collapsed two different questions")
	}
}

func TestExactOnlyRejectsParaphrases(t *testing.T) {
	pairs := paraphrasePairs()
	if len(pairs) == 0 {
		t.Skip("no paraphrase pairs in the corpus")
	}
	a, b := pairs[0][0], pairs[0][1]
	c := build(Config{ExactOnly: true}, a)
	if !c.Lookup("  "+a+"?  ", find(a).Tenant).Hit {
		t.Error("exact mode missed a punctuation-and-spacing-only variant")
	}
	if c.Lookup(b, find(a).Tenant).Hit {
		t.Errorf("exact mode served the paraphrase %q for %q; then it is not exact", b, a)
	}
}

func TestExactOnlyIgnoresTheThreshold(t *testing.T) {
	pairs := paraphrasePairs()
	if len(pairs) == 0 {
		t.Skip("no paraphrase pairs in the corpus")
	}
	c := build(Config{ExactOnly: true, Threshold: 0.01}, pairs[0][0])
	if c.Lookup(pairs[0][1], find(pairs[0][0]).Tenant).Hit {
		t.Error("ExactOnly must short-circuit before the threshold is consulted")
	}
}

func TestHigherThresholdNeverAdmitsMore(t *testing.T) {
	all := texts()
	stored, probes := all[:60], all[60:140]
	prev := 1 << 30
	for _, thr := range []float64{0.2, 0.4, 0.6, 0.8, 0.95} {
		c := build(Config{Threshold: thr}, stored...)
		hits := 0
		for _, p := range probes {
			if c.Lookup(p, find(p).Tenant).Hit {
				hits++
			}
		}
		if hits > prev {
			t.Errorf("threshold %v admitted %d, more than the looser cut's %d; the "+
				"admission rule is not monotone in the threshold", thr, hits, prev)
		}
		prev = hits
	}
}

func TestTenantScopingIsolatesIdenticalWording(t *testing.T) {
	txt, tenants := identicalWordingTrap()
	if txt == "" {
		t.Fatal("the corpus has no identical-wording trap; section 5 has nothing to prove")
	}
	m := model()

	unscoped := New(m, Config{Threshold: 0.6})
	scoped := New(m, Config{Threshold: 0.6, ScopeByTenant: true})
	for _, c := range []*Cache{unscoped, scoped} {
		q := findFor(txt, tenants[0])
		e := entry(txt, q.Tenant, q.Intent)
		e.Vec = m.Embed(txt)
		c.Put(e)
	}

	victim := findFor(txt, tenants[1])
	r := unscoped.Lookup(txt, tenants[1])
	if !r.Hit {
		t.Fatal("the unscoped cache did not leak; the trap is not wired correctly")
	}
	if r.Entry.Intent == victim.Intent {
		t.Fatal("the trap's two tenants share an intent, so it proves nothing")
	}
	if scoped.Lookup(txt, tenants[1]).Hit {
		t.Error("the scoped cache served one tenant's answer to another")
	}
	if !scoped.Lookup(txt, tenants[0]).Hit {
		t.Error("scoping broke the same-tenant hit it is supposed to preserve")
	}
}

func TestScopingDoesNotAffectSingleTenantTraffic(t *testing.T) {
	var acme []string
	for _, q := range corpus.All() {
		if q.Tenant == "acme" {
			acme = append(acme, q.Text)
		}
	}
	if len(acme) < 20 {
		t.Skip("not enough single-tenant texts")
	}
	a := build(Config{Threshold: 0.6}, acme[:15]...)
	b := build(Config{Threshold: 0.6, ScopeByTenant: true}, acme[:15]...)
	for _, p := range acme[15:] {
		if a.Lookup(p, "acme").Hit != b.Lookup(p, "acme").Hit {
			t.Errorf("scoping changed a decision inside one tenant for %q", p)
		}
	}
}

func TestGuardVetoesAConfusablePair(t *testing.T) {
	a, b := confusablePair()
	if a == "" {
		t.Fatal("no confusable pair in the corpus; the whole project needs one")
	}
	m := model()
	plain := New(m, Config{Threshold: 0.5})
	guarded := New(m, Config{Threshold: 0.5, Guard: true, GuardTopK: 3, GuardJaccard: 0.67})
	for _, c := range []*Cache{plain, guarded} {
		q := find(a)
		e := entry(a, q.Tenant, q.Intent)
		e.Vec = m.Embed(a)
		e.Rare = c.decisiveTokens(a)
		c.Put(e)
	}
	if !plain.Lookup(b, find(a).Tenant).Hit {
		t.Skipf("%q and %q do not collide at 0.5; nothing for the guard to veto", a, b)
	}
	r := guarded.Lookup(b, find(a).Tenant)
	if r.Hit {
		t.Errorf("the guard failed to veto the confusable %q -> %q", b, a)
	}
	if !r.GuardRejected {
		t.Error("the veto was not reported, so the sweep cannot count it")
	}
	if r.RejectedEntry.Text != a {
		t.Errorf("the vetoed entry was not reported; got %q", r.RejectedEntry.Text)
	}
}

// This test failed when it was first written, and the failure was the most
// useful result in the project.
//
// Section 4's sweep prices the top-K veto at about one point of aggregate hit
// rate, which reads as almost free. Measured here - on the population the veto
// actually adjudicates, one stored phrasing and a different phrasing of the
// same question - it rejects roughly four out of five. Both numbers are
// correct. The aggregate barely moves because ~82% of cache hits in the
// workload are verbatim repeats, whose decisive sets are identical and which
// the veto passes for free.
//
// So this test asserts the real property rather than the comfortable one: the
// veto is severe on paraphrases, and anyone reading only the aggregate will not
// know it. If a future change makes the guard gentle here, section 4's
// narrative is wrong and must be rewritten.
func TestGuardIsSevereOnTheParaphrasePopulation(t *testing.T) {
	pairs := paraphrasePairs()
	if len(pairs) < 50 {
		t.Fatalf("only %d paraphrase pairs; the measurement would be noise", len(pairs))
	}
	m := model()

	rate := func(cfg Config) float64 {
		hit := 0
		for _, p := range pairs {
			c := New(m, cfg)
			q := find(p[0])
			e := entry(p[0], q.Tenant, q.Intent)
			e.Vec = m.Embed(p[0])
			e.Rare = c.decisiveTokens(p[0])
			c.Put(e)
			if c.Lookup(p[1], q.Tenant).Hit {
				hit++
			}
		}
		return float64(hit) / float64(len(pairs))
	}

	plain := rate(Config{Threshold: 0.35})
	guarded := rate(Config{Threshold: 0.35, Guard: true, GuardTopK: 3, GuardJaccard: 0.67})

	if plain <= 0.1 {
		t.Fatalf("unguarded paraphrase retention is %.1f%%; the threshold, not the "+
			"guard, is doing the rejecting and this test measures nothing", 100*plain)
	}
	lost := (plain - guarded) / plain
	if lost < 0.5 {
		t.Errorf("the guard rejected only %.0f%% of paraphrase hits (%.1f%% -> %.1f%%). "+
			"That is good news, but section 4's central caveat is then false and the "+
			"report must be rewritten.", 100*lost, 100*plain, 100*guarded)
	}
}

// The other half of the same measurement: the severity has to buy something.
func TestGuardEliminatesConfusableHits(t *testing.T) {
	pairs := confusablePairs()
	if len(pairs) < 50 {
		t.Fatalf("only %d confusable pairs", len(pairs))
	}
	m := model()
	rate := func(cfg Config) float64 {
		hit := 0
		for _, p := range pairs {
			c := New(m, cfg)
			q := find(p[0])
			e := entry(p[0], q.Tenant, q.Intent)
			e.Vec = m.Embed(p[0])
			e.Rare = c.decisiveTokens(p[0])
			c.Put(e)
			if c.Lookup(p[1], q.Tenant).Hit {
				hit++
			}
		}
		return float64(hit) / float64(len(pairs))
	}
	plain := rate(Config{Threshold: 0.35})
	guarded := rate(Config{Threshold: 0.35, Guard: true, GuardTopK: 3, GuardJaccard: 0.67})
	if plain < 0.05 {
		t.Fatalf("only %.1f%% of confusable pairs collide unguarded; there is nothing "+
			"for the guard to prevent and the corpus is too easy", 100*plain)
	}
	if guarded > 0.01 {
		t.Errorf("the guard still serves %.1f%% of confusable pairs (was %.1f%%)",
			100*guarded, 100*plain)
	}
}

// At K=1 a Jaccard of anything in (0,1] decides identically, because a
// one-element set intersects another either fully or not at all. Worth pinning:
// it is why the sweep shows four identical rows, and a reader who does not know
// it will think the sweep is broken.
func TestJaccardIsInertAtKEqualsOne(t *testing.T) {
	m := model()
	pairs := paraphrasePairs()[:40]
	ref := -1
	for _, j := range []float64{0.2, 0.34, 0.5, 0.67, 1.0} {
		hit := 0
		for _, p := range pairs {
			c := New(m, Config{Threshold: 0.35, Guard: true, GuardTopK: 1, GuardJaccard: j})
			q := find(p[0])
			e := entry(p[0], q.Tenant, q.Intent)
			e.Vec = m.Embed(p[0])
			e.Rare = c.decisiveTokens(p[0])
			c.Put(e)
			if c.Lookup(p[1], q.Tenant).Hit {
				hit++
			}
		}
		if ref == -1 {
			ref = hit
		} else if hit != ref {
			t.Errorf("K=1 J=%v gave %d hits, but J is supposed to be inert at K=1 (got %d before)",
				j, hit, ref)
		}
	}
}

func TestJaccardMatchesItsDefinition(t *testing.T) {
	cases := []struct {
		a, b []string
		want float64
	}{
		{nil, nil, 1},
		{[]string{"x"}, nil, 0},
		{[]string{"x"}, []string{"x"}, 1},
		{[]string{"x", "y"}, []string{"x"}, 0.5},
		{[]string{"x", "y"}, []string{"y", "x"}, 1},
		{[]string{"a", "b"}, []string{"c", "d"}, 0},
	}
	for _, c := range cases {
		if got := Jaccard(c.a, c.b); got != c.want {
			t.Errorf("Jaccard(%v,%v) = %v, want %v", c.a, c.b, got, c.want)
		}
	}
}

func TestJaccardIgnoresDuplicates(t *testing.T) {
	if Jaccard([]string{"x", "x", "y"}, []string{"x", "y", "y"}) != 1 {
		t.Error("Jaccard is counting multiplicity; it is defined on sets")
	}
}

func TestDecisiveTokensAreStableAndBounded(t *testing.T) {
	c := build(Config{Threshold: 0.6, Guard: true, GuardTopK: 3})
	for _, txt := range texts()[:40] {
		a := c.DecisiveTokens(txt)
		if len(a) > 3 {
			t.Errorf("GuardTopK=3 but %q produced %d tokens: %v", txt, len(a), a)
		}
		for i := 0; i < 3; i++ {
			b := c.DecisiveTokens(txt)
			if len(a) != len(b) {
				t.Fatalf("DecisiveTokens(%q) is not deterministic", txt)
			}
			for j := range a {
				if a[j] != b[j] {
					t.Fatalf("DecisiveTokens(%q) is not deterministic", txt)
				}
			}
		}
	}
}

func TestEmptyCacheAlwaysMisses(t *testing.T) {
	c := New(model(), cfg())
	for _, txt := range texts()[:20] {
		if c.Lookup(txt, "acme").Hit {
			t.Fatalf("an empty cache returned a hit for %q", txt)
		}
	}
}

func TestLookupReturnsTheBestCandidateNotTheFirst(t *testing.T) {
	m := model()
	c := New(m, Config{Threshold: 0.0})
	ts := texts()[:30]
	for _, txt := range ts {
		q := find(txt)
		e := entry(txt, q.Tenant, q.Intent)
		e.Vec = m.Embed(txt)
		c.Put(e)
	}
	probe := ts[17]
	r := c.Lookup(probe, find(probe).Tenant)
	best := 0.0
	for _, txt := range ts {
		if find(txt).Tenant != find(probe).Tenant {
			continue
		}
		if s := embed.Cosine(m.Embed(probe), m.Embed(txt)); s > best {
			best = s
		}
	}
	if r.Similarity < best-1e-9 {
		t.Errorf("Lookup returned similarity %v but %v was available", r.Similarity, best)
	}
}

func TestDedupSortsAndUniques(t *testing.T) {
	got := dedup([]string{"c", "a", "b", "a", "c"})
	want := []string{"a", "b", "c"}
	if len(got) != len(want) {
		t.Fatalf("dedup gave %v", got)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("dedup gave %v, want %v", got, want)
		}
	}
}
