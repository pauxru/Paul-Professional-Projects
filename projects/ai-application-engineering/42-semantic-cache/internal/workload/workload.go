// Package workload turns the labelled corpus into a stream of timed arrivals.
//
// Everything here is deterministic given a seed. That is not fastidiousness:
// the whole report compares configurations, and a comparison between two
// configurations run on two different workloads measures nothing. The same
// seed produces byte-identical arrivals, so every difference in the report is
// attributable to the configuration under test.
//
// # What makes a workload worth measuring
//
// A cache benchmark on uniformly random queries is a benchmark of the hash
// table. Three properties are what make a support-desk stream interesting, and
// all three are modelled here:
//
//  1. Skew. A handful of intents are most of the traffic (Zipf). Without skew
//     there is nothing to cache; with it, hit rate is dominated by the head and
//     the interesting failures live in the tail.
//
//  2. Bursts. Real questions arrive in herds - an outage, a billing run, a
//     pricing change - so many near-simultaneous arrivals ask the SAME thing in
//     DIFFERENT words. This is exactly the condition under which semantic
//     coalescing is tempting and exactly the condition under which it is
//     dangerous, because a burst is where the confusable variants collide.
//
//  3. Paraphrase. Within an intent, the surface form varies. If every arrival
//     for an intent used identical text, an exact-match cache would score as
//     well as a semantic one and the project would have no subject.
package workload

import (
	"math"
	"sort"

	"semcache/internal/corpus"
	"semcache/internal/gateway"
)

// Config parameterises generation.
type Config struct {
	// N is the number of requests.
	N int
	// Seed fixes the stream.
	Seed uint64
	// Zipf is the popularity exponent over intents. 0 is uniform; ~1.0 is the
	// classic heavy head.
	Zipf float64
	// BurstFraction is the share of generation steps that produce a burst.
	BurstFraction float64
	// BurstSizeMin/Max bound the number of arrivals in one burst.
	BurstSizeMin, BurstSizeMax int
	// SpanUs is the total virtual duration of the stream.
	SpanUs int64
	// BurstSpreadUs is how tightly a burst's arrivals are packed. Smaller
	// means more overlap and more coalescing opportunity.
	BurstSpreadUs int64
	// SiblingRate is the chance that an arrival inside a burst asks a
	// CONFUSABLE sibling of the burst topic instead of the topic itself.
	SiblingRate float64
	// OneIntentPerFamily restricts the stream to a single representative of
	// each confusable family. This is what "benign traffic" actually means:
	// not that the traps are absent from the corpus, but that these users
	// happen never to ask about both Germany and France. The cache, the
	// corpus, the threshold and the stream size are unchanged - only who is
	// asking. It is the cleanest available lever on traffic mix.
	OneIntentPerFamily bool
	// AdversarialBoost multiplies the popularity of adversarial (trap) intents
	// so the hard cases are actually exercised. Reported alongside the results,
	// because it inflates the error rate relative to a benign stream and it
	// would be dishonest to leave it implicit.
	AdversarialBoost float64
}

// Default is the configuration used by the report.
func Default() Config {
	return Config{
		N:                4000,
		Seed:             0x5EEDCAC4E,
		Zipf:             0.9,
		BurstFraction:    0.45,
		BurstSizeMin:     4,
		BurstSizeMax:     14,
		SpanUs:           60_000_000, // one virtual minute
		BurstSpreadUs:    40_000,
		SiblingRate:      0.35,
		AdversarialBoost: 2.0,
	}
}

// rng is splitmix64: tiny, well-distributed, and reproducible across machines
// and Go versions. math/rand's stream is a documented implementation detail;
// results that must be identical forever should not depend on it.
type rng struct{ s uint64 }

func (r *rng) next() uint64 {
	r.s += 0x9E3779B97F4A7C15
	z := r.s
	z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9
	z = (z ^ (z >> 27)) * 0x94D049BB133111EB
	return z ^ (z >> 31)
}

func (r *rng) f64() float64 { return float64(r.next()>>11) / float64(1<<53) }

func (r *rng) intn(n int) int {
	if n <= 0 {
		return 0
	}
	return int(r.next() % uint64(n))
}

// Generate builds the arrival stream.
func Generate(cfg Config) []gateway.Request {
	r := &rng{s: cfg.Seed}

	// Group the corpus by intent so a chosen intent can be realised as any of
	// its paraphrases.
	byIntent := map[string][]corpus.Query{}
	for _, q := range corpus.All() {
		byIntent[q.Intent] = append(byIntent[q.Intent], q)
	}
	intents := make([]string, 0, len(byIntent))
	for k := range byIntent {
		intents = append(intents, k)
	}
	sort.Strings(intents) // map iteration order must not leak into the stream

	if cfg.OneIntentPerFamily {
		// Keep the first intent of each family in sorted order, so the choice
		// of representative is deterministic and not a hidden parameter.
		seen := map[string]bool{}
		kept := intents[:0:0]
		for _, it := range intents {
			f := corpus.Family(it)
			if f == "" {
				kept = append(kept, it)
				continue
			}
			if !seen[f] {
				seen[f] = true
				kept = append(kept, it)
			}
		}
		intents = kept
	}

	// Zipf weights over a fixed, sorted rank order, with traps boosted.
	weights := make([]float64, len(intents))
	total := 0.0
	for i, it := range intents {
		w := 1 / math.Pow(float64(i+1), cfg.Zipf)
		if byIntent[it][0].Adversarial {
			w *= cfg.AdversarialBoost
		}
		weights[i] = w
		total += w
	}
	cum := make([]float64, len(intents))
	acc := 0.0
	for i, w := range weights {
		acc += w / total
		cum[i] = acc
	}
	pick := func() string {
		u := r.f64()
		i := sort.SearchFloat64s(cum, u)
		if i >= len(intents) {
			i = len(intents) - 1
		}
		return intents[i]
	}

	tenants := corpus.Tenants()
	// realise turns an intent into a concrete request: a paraphrase, a tenant,
	// and an arrival time.
	realise := func(intent string, at int64) gateway.Request {
		qs := byIntent[intent]
		q := qs[r.intn(len(qs))]
		tenant := q.Tenant
		if corpus.Shared(intent) {
			// A platform-wide answer can be asked by anyone. This is what
			// creates the hit-rate cost of tenant scoping.
			tenant = tenants[r.intn(len(tenants))]
		}
		return gateway.Request{Text: q.Text, Tenant: tenant, Intent: intent, ArriveUs: at}
	}

	fam := familyIndex(intents)
	if cfg.OneIntentPerFamily {
		fam = map[string][]string{}
	}

	reqs := make([]gateway.Request, 0, cfg.N)
	for len(reqs) < cfg.N {
		at := int64(r.f64() * float64(cfg.SpanUs))
		if r.f64() < cfg.BurstFraction {
			// A burst: one topic, many phrasings, tightly packed. Within a
			// burst the CONFUSABLE siblings of the topic also appear, because
			// a pricing change provokes "is the free tier limit changing" and
			// "is the pro tier limit changing" in the same five minutes.
			intent := pick()
			size := cfg.BurstSizeMin + r.intn(cfg.BurstSizeMax-cfg.BurstSizeMin+1)
			sibs := fam[corpus.Family(intent)]
			for i := 0; i < size && len(reqs) < cfg.N; i++ {
				it := intent
				if len(sibs) > 1 && r.f64() < cfg.SiblingRate {
					it = sibs[r.intn(len(sibs))]
				}
				jitter := int64(r.f64() * float64(cfg.BurstSpreadUs))
				reqs = append(reqs, realise(it, at+jitter))
			}
			continue
		}
		reqs = append(reqs, realise(pick(), at))
	}

	sort.SliceStable(reqs, func(i, j int) bool { return reqs[i].ArriveUs < reqs[j].ArriveUs })
	return reqs
}

// familyIndex maps each confusable family to its member intents, in sorted
// order so selection within a family is reproducible.
func familyIndex(all []string) map[string][]string {
	out := map[string][]string{}
	for _, it := range all {
		if f := corpus.Family(it); f != "" {
			out[f] = append(out[f], it)
		}
	}
	return out
}

// Summary describes a generated stream, so the report can state what was
// measured rather than asking the reader to trust the generator.
type Summary struct {
	N                int
	DistinctIntents  int
	DistinctTexts    int
	TopIntentShare   float64
	AdversarialShare float64
	SharedShare      float64
	MaxOverlap       int
}

// Describe computes the summary. MaxOverlap is the largest number of requests
// arriving within one window, i.e. the worst herd the coalescer will face.
func Describe(reqs []gateway.Request, windowUs int64) Summary {
	s := Summary{N: len(reqs)}
	intents := map[string]int{}
	texts := map[string]bool{}
	adv := map[string]bool{}
	for _, q := range corpus.Adversarial() {
		adv[q.Intent] = true
	}
	nadv, nshared := 0, 0
	for _, r := range reqs {
		intents[r.Intent]++
		texts[r.Text] = true
		if adv[r.Intent] {
			nadv++
		}
		if corpus.Shared(r.Intent) {
			nshared++
		}
	}
	s.DistinctIntents = len(intents)
	s.DistinctTexts = len(texts)
	top := 0
	for _, c := range intents {
		if c > top {
			top = c
		}
	}
	if len(reqs) > 0 {
		s.TopIntentShare = float64(top) / float64(len(reqs))
		s.AdversarialShare = float64(nadv) / float64(len(reqs))
		s.SharedShare = float64(nshared) / float64(len(reqs))
	}

	// Sliding window over the sorted arrivals.
	j := 0
	for i := range reqs {
		for reqs[i].ArriveUs-reqs[j].ArriveUs > windowUs {
			j++
		}
		if n := i - j + 1; n > s.MaxOverlap {
			s.MaxOverlap = n
		}
	}
	return s
}
